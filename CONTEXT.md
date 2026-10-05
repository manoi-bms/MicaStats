# MicaStats

MicaStats provides desktop monitoring, MicaPad notes, and private AI assistance. Its meeting assistant helps the user follow a conference and prepare answers.

## Language

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
