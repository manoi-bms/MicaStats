# MicaStats

MicaStats provides desktop monitoring, MicaPad notes, and private AI assistance. Its meeting assistant helps the user follow a conference and prepare answers.

## Language

**StackChan companion**:
An optional nearby M5Stack robot that reflects MicaStats activity through its face, lights,
and deliberate head gestures. The custom UIFlow companion can also show live system readings.

**Presence detection**:
The StackChan camera, analysed in memory on the PC's GPU, telling whether the owner is at
the desk. It counts and locates faces only; it never identifies anyone.

**Away**:
Frames keep arriving but no face has been seen for a while. A covered camera or silent stream
is *unknown*, never away.

**Onlooker**:
A second face staying in view behind the owner, which guards MicaPad. A face that never moves
(a poster) stops counting.

**Direction check**:
A one-time head nudge that learns which way the head must turn, by watching the owner's face
move in the picture.

**USB companion session**:
A wired connection to the custom UIFlow robot program. Starting a temporary companion
replaces its current running program until the robot restarts, while preserving firmware,
saved robot files and servo calibration.

**Quiet mode**:
A companion preference that dims its lights and prevents new head movements. A movement
already accepted by the robot may still finish.

**Turzx display**:
An optional USB-C IPS secondary screen (such as the 3.5-inch `USB35INCHIPSV2` panel) that renders
live system telemetry, thermal gauges, storage/network activity, or a desk clock over USB serial.
Only hardware readings and public alert metrics are drawn; note text, prompts, and transcripts stay
on this PC.

**App language**:
The remembered language of menus, settings, dialogs and generated UI status: Thai by default,
or English. It is separate from note content, Markdown syntax, stored dates and AI response language.

**About**:
The Settings section containing the app's installed version, feature overview, project/support
links and attribution, illustrated by an embedded banner available offline.

**Open note**:
A note held in a MicaPad tab, including tabs in other MicaPad windows.

**Current note**:
The note displayed in a particular MicaPad window. Highlighting a navigation result does not
make it the current note until the user chooses it.

**Open notes picker**:
A searchable list of open notes, identified by title and file location or scratch-note number.

**Note color**:
A visual cue belonging to a note, shown in its tab and the Open notes picker. Renaming or moving
the note keeps its color; changing the color affects appearance only.

**Task**:
A Markdown checkbox item in a MicaPad note, either pending or completed.

**Markdown split preview**:
A view with the editable original note on the left and a live, read-only rendering on the right.
It changes presentation only. Its remembered divider ratio is separate from utility-pane width;
Search, AI and History temporarily replace it. History's version preview remains a separate view.
Source and preview scrolling follows matching content in both directions, using local source/render
anchors. Preview updates follow the current source view without editing the document or moving its
caret/selection; scroll synchronization itself does not rebuild the rendered document.

**Task dates**:
The time tracking first recorded a task and the time it was completed. Reopening keeps its
created date and clears its finished date; completing it again records the new completion time.
Existing tasks with unknown history start tracking when first observed.
These dates are task information stored separately from the editable note, displayed as read-only
labels. The task's date editor allows explicit start/finish corrections; setting a finish completes
the task and clearing it reopens the task. Copying task text does not copy its tracked dates.

**Assistant review**:
Task changes or credential storage proposed by Ask AI for its selected text or current note.
The user chooses which changes to apply. Editing the note makes a review stale; a new request
prepares a fresh review. Reading or proposing changes never applies them.

**Credential candidate**:
A possible password or secret found locally beside an explicit label or in a password-bearing
URL. Candidates appear with a label and source location while their values remain hidden.
Detection can miss unlabelled secrets; approving storage replaces exact copies in the current note.

**Meeting session**:
A period the user explicitly starts and stops to transcribe and analyze a conference.
_Avoid_: Background listener, always-on recorder

**Microphone source**:
The selected microphone supplying the user's side of a meeting session. Nearby voices and speaker playback may also be audible through it.

**Output source**:
The selected playback device supplying the audio heard by the user. It can contain conference participants and sounds from other applications.
_Avoid_: Remote participant, isolated conference audio

**Live transcript**:
The text recognized from the microphone and output sources during a meeting session. Source labels distinguish those two sources, not individual attendees.

**Meeting analysis**:
A running summary of the conversation, its questions, and its relevant points, shown privately to the user.

**ASR comparison**:
Two recognizers interpreting the same audio. Matching readings appear once; different readings remain alternative interpretations of one transcript segment, not independent evidence that a fact is correct.

**AI response language**:
The language used for summaries and suggested answers. The user can follow the conversation language or select Thai or English; this does not change the original transcript.

**Suggested answer**:
A private draft response grounded in the meeting transcript and any reference notes the user selected. The user decides whether and how to share it.
_Avoid_: Automatic reply

**Reference notes**:
MicaPad notes explicitly selected by the user to inform a meeting session's analysis and suggested answers.

**Saved transcript**:
A local transcript retained automatically for recovery or exported explicitly as a Markdown report. Automatic recovery contains transcript evidence only; neither form saves source audio.

**Transcript recovery**:
The latest nonempty saved transcript restored in a stopped state when Meeting Assistant opens. Recovery does not start capture, network requests, or AI work.

**Speech output**:
Audio synthesized from text, such as a suggested answer, for the user to hear.
_Avoid_: Recording, automatic reply

**Playback device**:
The output device on which speech output is heard. It can be the same device as the meeting's output source, in which case the speech is also audible to that source.
