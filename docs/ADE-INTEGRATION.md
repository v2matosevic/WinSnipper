# Optional screenshot attachments to Hephaestus

WinSnipper 0.9.0 can attach a final PNG screenshot to a named Hephaestus workspace and agent draft. The receiving app owns the image bytes and shows a preview. Existing draft text stays in place, and WinSnipper never submits an agent turn automatically.

This feature requires a Hephaestus build advertising capture protocol version 1. The public ADE 0.0.108 release and the installed 0.0.104 build on the maintainer's PC do not include that receiver as of 2026-10-03. The optional attachment controls report its absence; ordinary capture, editing, history, clipboard and recording remain available. Installing WinSnipper alone does not activate the receiver or update another application.

## Use with a compatible receiver

1. Open the receiving agent tile in Hephaestus.
2. Choose ADE in WinSnipper's screenshot editor, Attach screenshot to ADE on a screenshot thumbnail, or the history attachment button.
3. Select the named workspace and agent, then attach the capture.
4. Review its preview in Hephaestus and send the draft there when ready.

Only PNG screenshots up to 8 MiB are supported in this first path. Video and generic shell targets are excluded. Editor and thumbnail exports freeze the final image before encoding it on a worker; clipboard delivery is independent.

## Recovery and local data

Uncertain or failed requests are kept under `%APPDATA%/WinSnipper/CaptureOutbox`, encrypted with the current Windows user's DPAPI. Retry saved sends the same operation ID, destination and payload. It does not follow the current focus or generate a second agent turn. An accepted receipt clears the provider's pending copy; the receiver keeps its own image.

Endpoint discovery reads the existing current-user Version2 local-services registry. WinSnipper verifies the local receiver's service/instance/protocol identity before uploading and uses a companion token, not an agent session token. There is no cloud screenshot upload in this path.

Pending requests should be reconciled before manually repeating an uncertain handoff. Unavailable destinations are not automatically retargeted. Capture history browses saved PNG/MP4 files; the encrypted outbox is specifically for pending attachment operations.

## Verification scope

The implementation was tested through real C# to native receiver requests with synthetic PNG/text, content hashes and duplicate receipt checks. The picker was rendered offscreen, and the receiving composer was checked in a browser fixture. No live private screenshot was uploaded and no installed Hephaestus application was changed by the WinSnipper release.
