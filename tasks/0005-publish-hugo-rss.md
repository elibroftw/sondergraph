---
id: 0005
title: Publish posts to Hugo markdown + RSS
status: pending
priority: high
labels: [publish, hugo, rss]
dependencies: [0003, 0004]
created: 2026-10-19
---

## Context

DESIGN.md §1, feature #2. Export the user's own posts as a Hugo-friendly static site with
a configurable directory structure, plus an RSS feed.

This is the one feature where the tool writes *outward* rather than reading inward, so it
has a distinct safety property: nothing here may corrupt `data/`.

## Acceptance criteria

- [ ] User-configurable output directory structure (e.g. by year, by platform, by tag),
      expressed as a template rather than hardcoded.
- [ ] Hugo-compatible frontmatter: `title`, `date`, `tags`, `draft`, `slug`.
- [ ] Media copied alongside posts with correct relative paths, or left as remote URLs —
      configurable, since the two have very different repo-size implications.
- [ ] Valid RSS feed generated for the user's own posts.
- [ ] Deterministic output: running twice with unchanged input produces byte-identical
      files (so the output is diffable and reviewable in git).
- [ ] Source platform preserved per post, so a post published to two platforms is not
      silently duplicated.
- [ ] Dry-run mode that writes nothing and reports what would change.

## Notes

Determinism matters more than it looks: users will keep the generated site in git, and
unstable output makes every build a spurious diff.

Cross-posting (same content on Mastodon and Bluesky) is the main modelling question —
decide whether it is one post with two sources or two posts. Leaning: one post, many
sources, with the export emitting one file.
