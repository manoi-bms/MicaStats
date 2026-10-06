# MicaStats

MicaStats provides desktop monitoring, MicaPad notes, and private AI assistance. Its meeting assistant helps the user follow a conference and prepare answers.

## Language

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

**Task dates**:
The time tracking first recorded a task and the time it was completed. Reopening keeps its
created date and clears its finished date; completing it again records the new completion time.
Existing tasks with unknown history start tracking when first observed.
These dates are task information stored separately from the editable note, displayed as read-only
labels. The task's date editor allows explicit start/finish corrections; setting a finish completes
the task and clearing it reopens the task. Copying task text does not copy its tracked dates.

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
