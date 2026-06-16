---
name: release-tag-format
description: "Disruptor.Surface release tags use the format `v0.1.0-preview.NN` (with the leading `v` and the full semver-style version). The release pipeline filters tags by this pattern; a bare `preview.NN` tag is ignored."
metadata: 
  node_type: memory
  type: project
  originSessionId: 1fd960d5-e72a-4f6c-ba01-0efccf0ddb5b
---

Release tags on this repo must be `v{Version}` where `{Version}` is the `<Version>` value from `Directory.Build.props` (e.g. `v0.1.0-preview.54`). The release pipeline keys on the leading `v` and the full `0.1.0-preview.NN` shape; tagging with the bare `preview.NN` short form (or omitting the `v`) doesn't trigger a build.

**Why:** caught when I tagged `preview.54` on 2026-05-12 and the user flagged that the pipeline would skip it.

**How to apply:** when finalising a preview, `git tag v0.1.0-preview.NN` matching the `Directory.Build.props` `<Version>`. Check `git tag -l | tail -3` against the running pattern before tagging if uncertain.
