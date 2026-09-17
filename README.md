# StrmExtract (fork)

Fork of [faush01/StrmExtract](https://github.com/faush01/StrmExtract), an
Emby plugin that scans `.strm` files with no media info yet and probes
them (`RefreshMetadata` with `EnableRemoteContentProbe`) to populate
resolution/codec/audio/subtitle info in Emby.

## What's different in this fork

When a folder holds several `.strm` files for the same title that only
differ by a quality suffix — e.g. `Movie (2020) - 01 - 2160p.strm` /
`Movie (2020) - 02 - 1080p.strm`, the naming convention written by the
Dispatcharr [`vod_manager`](https://github.com/oxios0x00/vod-manager)
plugin — only the **first** one (alphabetically, which is also the best
quality with that naming scheme) is probed. The rest are skipped.

**Why**: this task probes every pending `.strm` sequentially, from the
same Emby server process — same client IP and user-agent for every
single request it makes, with no delay between items (the original
code's `Thread.Sleep(5000)` between items is commented out and there is
no setting to re-enable any pacing). When two of those requests are for
different quality versions of the *same* title, in close succession,
Dispatcharr's own VOD proxy can serve the second request the first
one's already-open stream instead of opening its own — its idle-session
reuse matches purely on `(content uuid, client ip, user-agent)`, never
on the specific relation/`stream_id` that was actually requested
(`apps/proxy/vod_proxy/multi_worker_connection_manager.py`,
`find_matching_idle_session`, matched by a client-IP/user-agent/content
score, not by `stream_id`). Confirmed happening in practice: Emby
permanently cached the wrong resolution/codec for the secondary version
of several movies after a single run of this task, verified against the
provider directly with `ffprobe` to rule out a real quality difference.

Filed upstream as [Dispatcharr#TODO](#) (fill in once opened) — the
real fix belongs there (idle-session reuse should also require the
requested `stream_id`/relation to match, not just the content+client).
This fork's change is a workaround on the consumer side: it avoids ever
sending two back-to-back requests for the same title's different
versions in the first place, at the cost of not getting Emby-native
MediaInfo for versions after the first. Their quality is still visible
in the filename itself.

If you don't use `vod_manager`'s `.strm` naming (`- NN - <quality>` or
a bare `- <quality>` suffix), this fork behaves identically to upstream
— nothing is grouped or skipped unless a filename actually matches that
exact pattern.
