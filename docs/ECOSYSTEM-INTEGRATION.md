# WinSnipper improvements and desktop integration

Prepared for Marko on 2026-10-02. This is a proposal grounded in the local source. Marko selected improving WinSnipper itself first, so capture history is the first implementation; cross-app delivery remains proposed. See [the history delivery record](CAPTURE-HISTORY.md) for its checks and installation state.

Connect captures to the work they explain. Start with a recent-captures view inside WinSnipper. The recommended next integration is an **Attach to Hephaestus** action that places the edited screenshot in a chosen workspace's draft. Extend that same workflow to Athena conversations and Work Hub task evidence after the first path works end to end.

Confidence is high in the source findings below, moderate in the recommended product order. The benefit has not been measured in daily use. Native behavior and current deployed API capabilities were not tested during this review.

## The connected workflow

1. Capture a region, window or screen with the existing shortcut.
2. Add arrows, steps, redactions and a short explanation.
3. Choose a destination displaying the app, workspace and conversation or task name.
4. Attach the final capture to that destination. A conversation receives an unsent draft so Marko can add or dictate instructions before sending.
5. Reopen the capture from history, the workspace or its linked task.

For example, a screenshot of a layout problem can go straight to the agent working on that site's workspace. A short recording can become evidence on a bug task. A before-and-after pair can remain attached to the completed work rather than being lost among timestamped files.

Suggested ownership:

| App | Responsibility in the connected workflow |
| --- | --- |
| WinSnipper | Capture, annotate, trim, export and browse recent captures. |
| Hephaestus | Select the coding workspace and receiving agent or terminal; stage the attachment alongside the instruction. |
| Athena Desktop | Provide the current tracked workspace/task and stage media in the existing Athena conversation UI. |
| Athena web | Store durable business task evidence and its access rules; retain the existing conversations and Work Hub records. |
| Talkty | Dictate the explanation into the receiving draft through its existing text workflow. |
| Hermes | Carry workspace identity and explicitly retained artifacts across devices through its existing workspace system. |

This proposal connects the current applications through shared identity, context and attachments. It does not decide whether their interfaces should eventually live in one executable. The September 4 pause on retiring Athena Desktop remains recorded in [UNIFICATION.md](../../Athena/app_tracker/docs/UNIFICATION.md); this request does not specify that the tracker should be retired.

## What the source already provides

| Capability | Finding and implication |
| --- | --- |
| Fast screenshot workflow | [SnipManager.cs](../src/SnipManager.cs) captures, crops, starts a background PNG save, copies the image and opens a thumbnail. Add integration after export so it does not delay capture. |
| Annotation and export | [EditorWindow.xaml.cs](../src/EditorWindow.xaml.cs) has arrows, shapes, pen, text, steps, pixelation, crop, OCR and a shared undo stack. `Composite()` is the authoritative final image; closing a dirty editor saves it. |
| File handoff | [FloatingThumb.xaml.cs](../src/FloatingThumb.xaml.cs) already supports dragging a saved capture into another application. Direct attachment should remove destination hunting while preserving this working route. |
| Hephaestus agent attachments | [AgentTile.svelte](../../Athena/app_tracker/apps/ade/src/lib/ade/AgentTile.svelte) supports image previews, image bytes and file links. [terminalsStore.svelte.ts](../../Athena/app_tracker/apps/ade/src/lib/ade/terminalsStore.svelte.ts) already queues attachments by tile ID. Reuse those delivery paths. |
| Terminal file drops | [WorkspaceTerminals.svelte](../../Athena/app_tracker/apps/ade/src/lib/ade/WorkspaceTerminals.svelte) distinguishes terminals from agent tiles when handling file drops. Preserve that distinction when routing external captures. |
| Athena conversation media | [agent-chat.ts](../../Athena/app_tracker/apps/desktop/src/lib/shared/api/agent-chat.ts) uploads through the paired device's existing conversation API after a capability check. [chat-attachment.ts](../../Athena/app_tracker/apps/desktop/src/lib/shared/utils/chat-attachment.ts) currently limits attachments to 10 MiB. The capability response reports one file per message. |
| Screen recording | WinSnipper captures and trims MP4 through its existing Media Foundation pipeline. Audio recording is listed as future work in [README.md](../README.md#roadmap). |
| Retention | [AutoCleanup.cs](../src/AutoCleanup.cs) can recycle old snips and recordings. A receiving app must own a durable copy before acknowledging an attachment. A link back to a disposable source is insufficient. |

Two details need explicit treatment:

- Athena's [ChatAttachment.php](../../Athena/athena/app/Services/Agent/ChatAttachment.php) classifies `video/mp4` and `video/webm` as audio. Accepting an MP4 file is therefore not proof that Athena can inspect its visual frames. Add a real visual-video path or a frame storyboard before offering clip analysis.
- The `/api/v1/agent/tasks/{task}/attachment` route resolves an attachment for an `AgentTask`, which is an agent execution record. It is not a Work Hub task upload endpoint. [Task.php](../../Athena/athena/app/Models/Task.php) already has attachments through `ClientPortalFile`; reuse the relevant storage infrastructure only after separating private internal evidence from client portal publication. The inspected Work Hub REST request/resource does not expose a direct capture-upload contract.

## WinSnipper improvements in priority order

| Order | Improvement | Result for Marko |
| --- | --- | --- |
| 1 | Recent captures with previews, search by date/name and local workspace labels | Find, reopen, copy or attach a capture after its floating thumbnail disappears. Start with the existing save folders. |
| 2 | Attach to a named Hephaestus workspace and draft | Give the agent the right image without switching between apps, hunting for a terminal or replacing the clipboard. |
| 3 | Saved annotation projects with selectable, movable objects | Reopen a screenshot and adjust an arrow, label or redaction rather than drawing the whole explanation again. Keep flattened PNG export for sharing. |
| 4 | Mic and system audio for short recordings | Explain a moving defect while demonstrating it. Make audio sources visible before recording and keep the capture shortcut responsive. |
| 5 | Clip size presets, frame storyboard and optional GIF export | Produce evidence suited to the destination's actual limits and media capabilities. Retain the full local recording. |
| 6 | Delayed capture, reuse the last region, color picker and highlighter | Make recurring design checks easier. Scrolling capture needs a separate design because arbitrary native apps and browsers scroll differently. |

History and direct attachment support the same first outcome. Audio, editable annotation projects and scrolling capture are substantial features with separate completion criteria; they should not be hidden inside the bridge work.

## Shared context and capture records

Use Hephaestus's workspace ID for local routing and Athena's repository ID or slug for durable cross-device association. A display name or a `B:\Coding` path is not a portable identity. Store the local path as device metadata only.

Capture the receiving context before WinSnipper's overlay takes focus. If Hephaestus or Athena Desktop supplies context, label its origin and freshness. A browser being foreground does not establish which client project it belongs to. When the last ADE workspace and the active timer disagree, display both and require an explicit destination choice. A suggested destination is not an automatic upload.

Proposed capture metadata:

| Field | Purpose |
| --- | --- |
| `capture_id`, `revision`, `created_at` | Identify the capture and distinguish the edited export from its original. |
| `kind`, MIME, byte length, content hash | Validate the media and acknowledge exactly the bytes received. |
| Width/height and optional duration | Render a useful preview and check destination limits. |
| Local source path | Locate the export on this machine. Keep it out of portable references. |
| Workspace ID and optional Athena repository/task ID | Associate evidence with the right work. Unassigned captures remain valid. |
| Optional caption and OCR text | Carry Marko's explanation and searchable text. Generate shared OCR from the final redacted export. |
| Destination receipt | Distinguish a saved capture, staged draft, uploaded attachment and sent message. |

Keep original pixels and editable annotation data local unless Marko explicitly chooses to share them. An integration export should contain only the final composited image. Later edits create another revision; they must not silently replace evidence already sent.

## Local attachment bridge

Add a narrow companion API to the receiving apps using the existing [local service registry](../../Athena/docs/ecosystem/local-services.md). Hermes already publishes `%LOCALAPPDATA%/Version2/services/<service>.json`, and Talkty's `VoiceEndpointResolver` consumes it. Reuse its loopback HTTP, live-process check, health identity and owner-readable companion token rather than introducing a second discovery system. ADE/Desktop registration is already tracked as Athena-7o1s; the reserved voice-command consumer is Athena-ovqe. This pass did not implement either.

The current [mcp_bridge.rs](../../Athena/app_tracker/apps/ade/src-tauri/crates/ade-core/src/mcp_bridge.rs) authenticates agent sessions with session tokens. WinSnipper is an independent tray app. It needs its own bounded companion identity, endpoint discovery and lifecycle; copying an agent's session token would give it the wrong authority.

The proposed companion contract has four operations:

1. List available destinations and their supported media/limits.
2. Read a suggested current workspace or conversation, including freshness and origin.
3. Stage a specific capture revision in a named destination.
4. Look up an operation receipt after a timeout or restart.

Protect the discovery record for the current user and issue a separate companion credential. Accept authenticated loopback calls only; reject browser-origin requests unless explicitly designed and authorized. Do not expose shell commands, arbitrary filesystem reads or agent execution through this API. Transfer media bytes into receiver-owned storage with declared size limits instead of asking the receiver to open any caller-supplied path.

For multiple ADE windows, route by the destination's owning window and native workspace identity. Include an operation ID and capture hash. Store the receipt durably before returning success. A repeated operation returns the original receipt instead of attaching the image twice; a changed payload with the same operation ID is rejected.

For an agent tile, feed the existing attachment queue. For a terminal, use the existing file-drop/paste route only when the terminal is an eligible coding-agent input surface and readiness is known. Do not inject a path into a generic shell, an active command or an arbitrary program. Enter remains Marko's action. Closing, busy or unsupported targets return a clear unavailable result and preserve the capture.

Keep bridge calls, hashing and file work on workers. WinSnipper's low-level keyboard hook and WPF UI thread must never wait on the other app. Review the existing thumbnail save-wait behavior before using it for asynchronous handoff; an expired wait must not be reported as a successful export.

## Implementation sequence and acceptance

### First delivery inside WinSnipper

Deliver local capture history before cross-app integration, following Marko's selected priority. Browse the current screenshot folder and its Recordings subfolder, filter by type, search filenames/dates and reopen captures through the existing editor or trimmer. Keep filesystem scanning and preview decoding away from capture's UI/hook path. Its implementation and verification belong in [CAPTURE-HISTORY.md](CAPTURE-HISTORY.md).

### Next delivery into Hephaestus

Implement the WinSnipper-to-ADE companion path for PNG screenshots. Offer attachment from the thumbnail, editor and history. Show the workspace and receiving conversation before the action. Preserve each target's existing draft text, and stage the final image without sending the message.

The finish is observable: capture, annotate, attach, then see the exact final pixels and filename in the selected ADE draft. The same capture remains available after its original source is recycled according to existing settings. Repeat delivery after a simulated timeout and confirm one attachment, one receipt and no submitted agent turn.

Required checks include both WinSnipper flavors in an isolated build location, focused bridge tests through the real local endpoint, receiver authorization/size validation, wrong-credential rejection, duplicate operation handling, source-save failure, two ADE windows, a disappearing target and unsupported terminal surfaces. Render changed WinSnipper states offscreen with `tools\ui-shots.ps1` and inspect the PNGs. Run the affected ADE frontend/native gates and document Mac limits. Marko performs the hands-on desktop pass.

### Athena conversation delivery

Add the same staging contract to Athena Desktop and reuse its existing attachment preview and paired conversation API. Retain pending media durably until it is removed or sent, because today's file drafts only survive while the chat view remains open. Obtain file count, byte limit and media support from the destination's advertised capabilities.

Prove that the selected thread receives the file once, other threads remain unchanged, failed sends retain the draft and no question is automatically submitted on reconnect. Upload only after the existing Send action.

### Work Hub task evidence

Add an owner-authorized internal task attachment contract using existing Athena storage and policy infrastructure where appropriate. Show recent task destinations associated with the selected workspace, and let Marko create a task with a caption and evidence in one flow. Keep client publication a separate explicit action.

Prove task ownership, workspace association, private visibility, retry deduplication and download integrity through the actual API. Cross-device links resolve by stored asset identity, not by the originating computer's file path. Business task records, agent execution records and coding implementation issues retain their existing separate roles.

### Visual recordings and wider app navigation

Introduce visual clip handling, audio capture and destination size presets as separate features. A storyboard with timestamps can provide a supported image-based route for visual evidence while the full MP4 remains an attachment for human playback. Display the actual destination capability; do not label audio-only processing as video understanding.

After capture delivery is proven, add workspace-aware Open in Athena / Open in Hephaestus links and a shared launcher. Reuse existing workspace and device identities. A single tray, updater or executable is a later product decision with reliability and packaging consequences, not a prerequisite for the connected workflow.

## Evidence and delivery state

Reviewed local source on 2026-10-02: WinSnipper `fc66fbd`, nested Athena Desktop/ADE repository `a242d2df`, Athena parent `ead6f26b6`. The Athena trees contain unrelated uncommitted work; this review did not edit them. Relevant code was read directly rather than inferred from version numbers or older release notes.

The root `B:/Coding/WinSnipper/AGENTS.md` and `B:/Coding/Athena/CLAUDE.md` are absent. The supplied AGENTS instructions, WinSnipper's CLAUDE/architecture/contribution documents, Athena's AGENTS instructions and nested app guidance were used.

Delivered integration work in this pass: this proposal and its README link. The separate local history implementation is recorded in CAPTURE-HISTORY.md. No integration was installed or published, no running application was stopped and no screenshot, conversation or business task was uploaded. Cross-app runtime checks belong to the implementation stages above.
