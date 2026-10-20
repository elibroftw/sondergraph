// Desktop shell for sondergraph.
//
// The shell is deliberately thin. It does one job: start the ASP.NET API as a sidecar, read
// the port it chose, point the webview at it, and make sure the sidecar dies with this
// process. All application behaviour lives in the backend and the web UI.
//
// Why a sidecar rather than in-process hosting: the backend is .NET and this shell is Rust,
// so they cannot share a process. The seam between them is HTTP, which is the same seam the
// browser version uses (ADR 0001, ADR 0002).
//
// The sidecar is the API SERVER, never the CLI. Shelling out to `sg` and parsing its console
// output is the failure mode ADR 0001 rejects.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::{BufRead, BufReader};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::thread;
use std::time::{Duration, Instant};

use serde::Deserialize;
use tauri::{RunEvent, WebviewUrl, WebviewWindowBuilder};

/// Port used in development, so the window has a stable address.
///
/// A release build deliberately uses an ephemeral port: the kernel picks a free one, which
/// avoids colliding with anything else the user is running. During development a fixed port
/// is more useful than collision-avoidance, because it means the same URL works in a browser,
/// in curl, and after a reload - and can be hard-coded in the dev config below.
#[cfg(debug_assertions)]
const DEV_PORT: u16 = 15190;

/// URL the window loads in development.
#[cfg(debug_assertions)]
const DEV_URL: &str = "http://127.0.0.1:15190/";

/// How long to wait for the backend to report a listening port.
///
/// Generous because a cold start of a self-contained .NET binary reads ~80 MB from disk,
/// and a first run may be scanned by antivirus. Ten seconds is well beyond a healthy start
/// on any machine, so reaching it means something is genuinely wrong.
const READY_TIMEOUT: Duration = Duration::from_secs(10);

/// One line of the backend's stdout handshake.
#[derive(Debug, Deserialize)]
#[serde(tag = "event")]
enum HandshakeEvent {
    #[serde(rename = "server.listening")]
    Listening {
        addr: String,
        pid: u32,
        protocol: u32,
    },

    #[serde(rename = "server.ready")]
    Ready {
        url: String,
        workspace: Option<String>,
        #[serde(rename = "workspaceOk")]
        workspace_ok: bool,
        #[serde(rename = "workspaceError")]
        workspace_error: Option<String>,
    },

    #[serde(rename = "server.error")]
    Error { code: String, message: String },
}

/// The running backend, kept so it can be killed on exit.
struct Backend {
    child: Mutex<Option<Child>>,
}

impl Backend {
    /// Terminates the sidecar and waits for it to actually die.
    ///
    /// Waiting matters: returning immediately would let this process exit while the child
    /// is still shutting down, and on some platforms the child then survives. The classic
    /// symptom of getting this wrong is a stray `sondergraph-server` holding a port after
    /// every launch, and users eventually accumulating dozens of them.
    fn shutdown(&self) {
        if let Ok(mut guard) = self.child.lock() {
            if let Some(mut child) = guard.take() {
                log_line(&format!("stopping backend (pid {})", child.id()));

                // Ignore the error: the process may already have exited on its own, which
                // is a normal race and not a failure worth surfacing.
                let _ = child.kill();

                // Bound the wait so a wedged backend cannot hang the shutdown forever.
                let deadline = Instant::now() + Duration::from_secs(5);
                loop {
                    match child.try_wait() {
                        Ok(Some(_)) => break,
                        Ok(None) if Instant::now() < deadline => {
                            thread::sleep(Duration::from_millis(50));
                        }
                        _ => break,
                    }
                }

                let _ = child.wait();
            }
        }
    }
}

fn log_line(message: &str) {
    // stderr rather than stdout: when launched from a terminal, stdout may be piped.
    eprintln!("[sondergraph] {message}");
}

/// Resolves the sidecar binary that Tauri placed next to this executable.
///
/// Tauri's `externalBin` copies bundled binaries beside the main executable and STRIPS the
/// `-<target-triple>` suffix, so the runtime name is plain `sondergraph-server`, while the
/// source file in `desktop/binaries/` must be `sondergraph-server-<triple>`.
fn sidecar_path() -> std::path::PathBuf {
    let exe = std::env::current_exe().expect("cannot resolve the current executable");
    let dir = exe.parent().expect("executable has no parent directory");

    let name = if cfg!(windows) {
        "sondergraph-server.exe"
    } else {
        "sondergraph-server"
    };

    dir.join(name)
}

/// Locates the built web UI in the repository, for development.
///
/// Walks up from this executable looking for `src/Sondergraph.Api/wwwroot`, which is where
/// `vite build` writes. Returns None when the UI has not been built, so the caller can warn
/// with the exact command rather than showing a blank window.
#[cfg(debug_assertions)]
fn dev_web_root() -> Option<std::path::PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let mut directory = exe.parent()?;

    // target/debug -> target -> src-tauri -> desktop -> repo root.
    for _ in 0..6 {
        let candidate = directory.join("src").join("Sondergraph.Api").join("wwwroot");

        if candidate.join("index.html").exists() {
            return Some(candidate);
        }

        directory = directory.parent()?;
    }

    None
}

/// Warns when the sidecar is older than the API source it was published from.
///
/// The sidecar and this shell are built separately, so a backend change is invisible until
/// `build-sidecar.sh` is re-run. That produced a genuinely confusing failure: the shell passed
/// `--port 15190`, a stale binary ignored the unknown flag and bound an ephemeral port, and
/// the window pointed at nothing. A warning at startup is cheaper than that debugging session.
#[cfg(debug_assertions)]
fn warn_if_sidecar_stale(sidecar: &std::path::Path) {
    let Some(web_root) = dev_web_root() else { return };

    // <repo>/src/Sondergraph.Api/wwwroot -> <repo>/src/Sondergraph.Api
    let Some(api_dir) = web_root.parent() else { return };
    let Ok(sidecar_modified) = sidecar.metadata().and_then(|m| m.modified()) else { return };

    // The newest .cs directly in the API project is a good proxy for "the source changed".
    let mut newest_source: Option<std::time::SystemTime> = None;

    let Ok(entries) = std::fs::read_dir(api_dir) else { return };

    for entry in entries.flatten() {
        let path = entry.path();

        if path.extension().and_then(|e| e.to_str()) != Some("cs") {
            continue;
        }

        let Ok(modified) = entry.metadata().and_then(|m| m.modified()) else { continue };

        if newest_source.is_none_or(|current| modified > current) {
            newest_source = Some(modified);
        }
    }

    if let Some(newest) = newest_source {
        if newest > sidecar_modified {
            log_line("warning: the sidecar is older than the API source.");
            log_line("warning: re-run desktop/build-sidecar.sh, or the backend may ignore new flags.");
        }
    }
}

/// Starts the backend and returns it along with where to point the webview.
///
/// Only the bundled path is implemented. The Tauri shell plugin's `<dev>` fallback was
/// removed deliberately: its spawned process is owned by the plugin, not by us, so it
/// cannot go through the same kill path. Shipping a launcher that half-supervises its
/// backend is how orphaned servers happen, so the copy step is mandatory instead (see
/// `build-sidecar.sh`).
fn start_backend() -> Result<(Child, String, Option<String>, bool, Option<String>), String> {
    let path = sidecar_path();

    if !path.exists() {
        return Err(format!(
            "the server component is missing.\n\nExpected it at:\n  {}\n\n             Publish it with desktop/build-sidecar.sh before building the desktop app.",
            path.display()
        ));
    }

    log_line(&format!("starting sidecar at {}", path.display()));

    #[cfg(debug_assertions)]
    warn_if_sidecar_stale(&path);

    let mut command = Command::new(&path);
    command.arg("--handshake");

    // In development the backend serves the UI straight out of the repository's build
    // output, rather than requiring a copy next to the sidecar. That copy was a manual step
    // that any `cargo build` silently invalidated, which is what produced a window saying
    // "no web UI is built" while the build directory looked fine.
    #[cfg(debug_assertions)]
    {
        command.arg("--port").arg(DEV_PORT.to_string());

        if let Some(web_root) = dev_web_root() {
            log_line(&format!("serving the web UI from {}", web_root.display()));
            command.arg("--web-root").arg(&web_root);
        } else {
            log_line(
                "warning: no built web UI found; run `npm --prefix web run build` in the repo root",
            );
        }
    }

    let mut child = command
        .stdout(Stdio::piped())
        // Inherit stderr so backend warnings reach the terminal when launched from one,
        // instead of vanishing into a pipe nobody reads.
        .stderr(Stdio::inherit())
        .spawn()
        .map_err(|e| format!("could not execute the server at {}: {e}", path.display()))?;

    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| "the server produced no stdout to read the handshake from".to_string())?;

    let reader = BufReader::new(stdout);

    match read_handshake(reader) {
        Ok(result) => Ok((child, result.0, result.1, result.2, result.3)),
        Err(e) => {
            // Do not leak a half-started backend if the handshake fails.
            let _ = child.kill();
            let _ = child.wait();
            Err(e)
        }
    }
}

/// Reads handshake lines from a child process's stdout.
fn read_handshake(
    reader: BufReader<std::process::ChildStdout>,
) -> Result<(String, Option<String>, bool, Option<String>), String> {
    let deadline = Instant::now() + READY_TIMEOUT;

    for line in reader.lines() {
        if Instant::now() > deadline {
            return Err("the backend did not report a ready state within 10 seconds".into());
        }

        let Ok(line) = line else { continue };

        if let Some(result) = interpret(&line)? {
            return Ok(result);
        }
    }

    Err("the backend exited before reporting a ready state".into())
}

/// Interprets one handshake line.
///
/// Returns `Ok(None)` for lines that are not handshake events (the backend may also log
/// there), and `Err` for a reported failure so the caller can show it verbatim.
fn interpret(line: &str) -> Result<Option<(String, Option<String>, bool, Option<String>)>, String> {
    let trimmed = line.trim();

    if !trimmed.starts_with('{') {
        // Not JSON: backend logging. Show it so a failure is diagnosable.
        log_line(trimmed);
        return Ok(None);
    }

    let event: HandshakeEvent = match serde_json::from_str(trimmed) {
        Ok(event) => event,
        Err(_) => return Ok(None),
    };

    match event {
        HandshakeEvent::Listening { addr, pid, protocol } => {
            log_line(&format!("backend listening on {addr} (pid {pid}, protocol {protocol})"));
            Ok(None)
        }

        HandshakeEvent::Ready { url, workspace, workspace_ok, workspace_error } => {
            log_line(&format!("backend ready at {url}"));
            Ok(Some((url, workspace, workspace_ok, workspace_error)))
        }

        HandshakeEvent::Error { code, message } => {
            Err(format!("the backend failed to start ({code}): {message}"))
        }
    }
}

/// Builds a self-contained error page.
///
/// A blank window with no explanation is the worst outcome for a launcher, so every failure
/// path renders this instead. It names the problem, the likely cause, and the command that
/// fixes it.
fn error_page(title: &str, detail: &str, hint: &str) -> String {
    let escape = |s: &str| {
        s.replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;")
            .replace('"', "&quot;")
    };

    format!(
        r#"<!doctype html>
<html><head><meta charset="utf-8"><meta name="color-scheme" content="light dark">
<title>sondergraph</title>
<style>
  :root {{ color-scheme: light dark; font-family: system-ui, sans-serif; }}
  body {{ margin: 0; padding: 40px; background: #fafaf7; color: #1a1a17; }}
  @media (prefers-color-scheme: dark) {{ body {{ background: #16181a; color: #e8e6e1; }} pre {{ background: #262a2e !important; }} }}
  h1 {{ font-size: 20px; margin: 0 0 12px; }}
  p {{ line-height: 1.5; max-width: 60ch; }}
  pre {{ background: #f0efea; padding: 12px; border-radius: 8px; overflow-x: auto; }}
  .muted {{ opacity: .7; font-size: 13px; }}
</style></head>
<body>
  <h1>sondergraph could not start</h1>
  <p><strong>{}</strong></p>
  <p>{}</p>
  <pre>{}</pre>
  <p class="muted">This is the desktop shell. The application itself runs as a local server
  that the window connects to.</p>
</body></html>"#,
        escape(title),
        escape(hint),
        escape(detail)
    )
}

/// Installs handlers so the backend is killed even on an abrupt signal.
///
/// Tauri's `RunEvent::Exit` covers a normal window close, but SIGTERM or SIGINT bypasses the
/// run loop entirely - which is how a desktop shell is usually terminated (the session
/// manager, a terminal Ctrl-C, or a package upgrade). Without this the sidecar is orphaned
/// and keeps holding its port. This was observed happening before it was added.
#[cfg(unix)]
fn install_signal_handlers(backend: std::sync::Arc<Backend>) {
    // Only async-signal-safe work is permitted inside a handler, so it just records that a
    // signal arrived; a thread does the actual shutdown.
    static SIGNALLED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

    extern "C" fn on_signal(_signal: libc::c_int) {
        SIGNALLED.store(true, std::sync::atomic::Ordering::SeqCst);
    }

    unsafe {
        libc::signal(libc::SIGTERM, on_signal as *const () as libc::sighandler_t);
        libc::signal(libc::SIGINT, on_signal as *const () as libc::sighandler_t);
        libc::signal(libc::SIGHUP, on_signal as *const () as libc::sighandler_t);
    }

    thread::spawn(move || loop {
        if SIGNALLED.load(std::sync::atomic::Ordering::SeqCst) {
            log_line("signal received; shutting down the backend");
            backend.shutdown();
            std::process::exit(0);
        }

        thread::sleep(Duration::from_millis(200));
    });
}

#[cfg(not(unix))]
fn install_signal_handlers(_backend: std::sync::Arc<Backend>) {
    // On Windows the console control handler and job object cover this; Tauri's runtime
    // exits through the normal path.
}

fn main() {
    let backend = std::sync::Arc::new(Backend {
        child: Mutex::new(None),
    });
    install_signal_handlers(backend.clone());
    let backend_for_setup = backend.clone();

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .setup(move |app| {


            match start_backend() {
                Ok((child, url, workspace, workspace_ok, workspace_error)) => {
                    // The handshake URL is authoritative in release. In development it is
                    // logged but not used for the window, which loads the fixed dev URL - so
                    // the value is read either way and only its use differs.
                    let _ = &url;
                    if let Ok(mut guard) = backend_for_setup.child.lock() {
                        *guard = Some(child);
                    }

                    log_line(&format!(
                        "workspace: {}",
                        workspace.clone().unwrap_or_else(|| "(none)".into())
                    ));

                    if !workspace_ok {
                        log_line(&format!(
                            "warning: no usable workspace: {}",
                            workspace_error.unwrap_or_else(|| "unknown".into())
                        ));
                    }

                    // In development the address is known ahead of time, so the window can
                    // be created pointing at the fixed port. The handshake is still awaited
                    // first, so a window never opens onto a server that failed to start.
                    #[cfg(debug_assertions)]
                    let target_url = DEV_URL.to_string();

                    #[cfg(not(debug_assertions))]
                    let target_url = url;

                    let parsed: tauri::Url = target_url
                        .parse()
                        .map_err(|e| format!("invalid URL '{target_url}': {e}"))?;

                    WebviewWindowBuilder::new(app, "main", WebviewUrl::External(parsed))
                        .title("sondergraph")
                        .inner_size(1180.0, 820.0)
                        .min_inner_size(720.0, 480.0)
                        .build()?;
                }
                Err(error) => {
                    // Never fail silently: show the reason in the window.
                    let page = error_page(
                        "The local server did not start.",
                        &error,
                        "Check that the installed application includes its server component, \
                         or run `dotnet run --project src/Sondergraph.Api` to see the backend's \
                         own error output.",
                    );

                    WebviewWindowBuilder::new(app, "error", WebviewUrl::External(
                        format!("data:text/html;charset=utf-8,{}", urlencode(&page))
                            .parse()
                            .expect("a data URL always parses"),
                    ))
                    .title("sondergraph — startup failed")
                    .inner_size(760.0, 480.0)
                    .build()?;
                }
            }

            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("failed to build the sondergraph desktop shell");

    // Shut the backend down on every exit path, not only on a clean window close.
    app.run(move |_handle, event| {
        if let RunEvent::ExitRequested { .. } | RunEvent::Exit = event {
            backend.shutdown();
        }
    });
}

/// Percent-encodes a string for use in a data URL.
fn urlencode(input: &str) -> String {
    let mut out = String::with_capacity(input.len() * 2);

    for byte in input.as_bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(*byte as char);
            }
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }

    out
}
