{
  # Development shell for building the Tauri desktop shell.
  #
  # Why this exists: Tauri 2 on Linux links against WebKitGTK 4.1 and libsoup 3, which are
  # not present in a stock Fedora 44 install and require `sudo dnf` to add system-wide.
  # This shell provides them for the build without touching the system, which means a
  # contributor (or CI) can build the desktop app with no root access at all.
  #
  # Usage:
  #   nix develop ./desktop --command cargo build --manifest-path desktop/src-tauri/Cargo.toml
  #
  # Or, interactively:
  #   nix develop ./desktop
  #   cd src-tauri && cargo build

  description = "sondergraph desktop build environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f (import nixpkgs { inherit system; }));
    in
    {
      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          name = "sondergraph-desktop";

          nativeBuildInputs = with pkgs; [
            pkg-config
            cargo
            rustc
            rustfmt
            clippy
            nodejs
          ];

          buildInputs = with pkgs; [
            # The Tauri 2 Linux dependency set.
            webkitgtk_4_1
            libsoup_3
            gtk3
            glib
            cairo
            pango
            gdk-pixbuf
            atk
            librsvg
            openssl

            # Runtime, for actually launching the built app in the shell.
            alsa-lib
            dbus
          ];

          # pkg-config needs to find the .pc files for the GTK stack.
          PKG_CONFIG_PATH = pkgs.lib.makeSearchPathOutput "dev" "lib/pkgconfig" (with pkgs; [
            webkitgtk_4_1
            libsoup_3
            gtk3
            glib
            cairo
            pango
            gdk-pixbuf
            atk
          ]);

          # WebKitGTK's WebProcess needs its helper libraries visible at runtime.
          LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath (with pkgs; [
            webkitgtk_4_1
            libsoup_3
            gtk3
            glib
            cairo
            pango
            gdk-pixbuf
            atk
            librsvg
            openssl
            alsa-lib
            dbus
          ]);

          # Tauri's build script and the webview both prefer to run on X11 in many CI
          # environments; Wayland still works where the compositor allows it.
          WEBKIT_DISABLE_COMPOSITING_MODE = "1";
        };
      });
    };
}
