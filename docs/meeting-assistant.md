# Meeting Assistant

Open **Meeting Assistant** from the MicaStats menu. Nothing is recorded until you choose **Start listening**.

First open **Configure services** or **Settings → Meeting**. Enter your compatible ASR and/or TTS API base URLs, including their API path, and save. All three addresses start empty. For example, `https://asr.example/v1` is a reserved-domain illustration, not a working service. Configure only the services you intend to use; transcription and speech have separate readiness checks. Saving new service addresses stops active voice work before applying them.

**One ASR endpoint is sufficient for single-service transcription.** Select ASR2 (Qwen) or ASR1 (Typhoon) to send audio only to that service. For concurrent comparison, configure both and select **ASR2 + ASR1**. This sends each captured chunk to both services and doubles ASR uploads. TTS is separate and optional for Speak.

Each service has a **Test connection** button that checks the address currently entered, before you save. ASR sends one second of generated silence and validates the transcription response; TTS sends the fixed phrase “Connection test.” and validates the returned WAV without playing it. No microphone audio, meeting content or notes are used. Inline results include elapsed time; **Cancel test**, editing the address or closing Settings cancels the request. Tests time out after 20 seconds, never save settings, and do not prove recognition quality or hardware audio operation.

| Setting | Compatible service | Routes appended to the base URL |
| --- | --- | --- |
| ASR2 | Qwen ASR, model `Qwen/Qwen3-ASR-1.7B` | `audio/transcriptions` |
| ASR1 | Typhoon ASR, model `typhoon-asr-realtime` | `audio/transcriptions` |
| TTS | VoxCPM, model `voxcpm-thai` | `audio/speech`, `voices` |

1. Choose your microphone and the output device that plays conference audio. The output source includes all applications playing on that device. Source labels identify the microphone and output, not individual attendees.
2. Choose **ASR2**, **ASR1**, or **ASR2 + ASR1** before starting. Each source uploads four-second WAV chunks. Comparison starts both requests concurrently and finalizes the segment after both complete or a provider fails/times out; it can therefore add latency. This is near-real-time transcription, not a subsecond stream.
3. Enable the Assistant and configure a model in **Settings → AI** to receive a summary, key points, detected questions and private suggested answers. In **Summary & answers**, choose **AI language → Follow transcript**, **Thai · ไทย**, or **English**. Follow transcript is the default and keeps Thai conversations in Thai; an explicit language selection controls the generated prose. The choice is remembered. Changing it clears old answers/prepared speech and refreshes active analysis without changing the original transcript. You can also type a question. Sources identify the transcript segments and selected note passages supporting an answer; uncited content is marked unsupported.
4. Select only the MicaPad reference notes you want to share with the configured AI provider. Removing a note clears derived answers. Storing text as a credential also clears retained note context and speech; selected notes must be read again through credential filtering.
5. Choose an answer for speech or enter text in the speech panel. Choose a playback device and press **Speak**. **Refresh voices** fetches the available presets explicitly. No voice samples are enrolled. **Stop speaking** cancels generation or playback.
6. Use **Stop session** to finish listening. Transcript recovery is saved automatically; **Save Markdown** remains available when you want a report containing the transcript and current AI analysis. Starting a new session asks for confirmation and keeps the previous meeting in **Saved transcripts**.

In dual mode, each audio chunk still produces one transcript segment. ASR2 is the preferred reading when nonempty; otherwise ASR1 supplies the text. Matching readings appear once. Disagreements keep a labeled ASR1 alternative; a missing or unavailable reading is stated explicitly. The AI receives both readings as alternatives from one source, with instructions to preserve uncertainty about names, numbers and negation. It does not automatically rewrite the original transcript or treat two recognizers as independent proof. Accuracy improvement is not guaranteed and has not been measured against labeled speech.

If one provider fails or reaches its thirty-second deadline, it is disabled for the rest of that meeting and the status shows the remaining provider. The healthy service continues without repeatedly waiting for the failed service. A successful empty result counts as silence, not a service failure. If both services fail, listening stops. Start a new session to retry both; save the current transcript first if needed. Both readings count toward the transcript size limit. No extra per-chunk AI call is made.

ASR and TTS use only the service addresses you configure locally. The app keeps captured and generated audio in memory and does not write it to its logs or configuration. It automatically persists transcript recovery files locally as described below. These controls do not establish how your chosen services retain requests.

Automatic analysis runs at most once every ten seconds when new transcript text exists. Each analysis or typed question uses one request from the shared daily AI allowance, including failed or cancelled attempts. Continuous analysis can use up to 360 requests per hour; the default daily allowance is 100. At the limit, analysis pauses while transcription continues. Change the allowance explicitly in Settings if needed. ASR and TTS do not consume the chat allowance.

Speech is generated while listening continues. Immediately before playback, MicaStats pauses both capture sources. It records a listening gap and resumes after playback and a short settling interval if the same meeting remains active. Conference speech during that gap is missed. Stopping the meeting prevents capture from resuming. Local speaker playback can also reach the conference application's microphone; headphones help. Speech is played locally, with no automatic reply or virtual microphone injection.

Speech input is limited to 4,096 characters and a single request/playback at a time. Generated audio is limited to 10 MiB and 90 seconds. Meetings stop visibly at eight hours, two million transcript characters, a full transcription queue, or a device failure or failure of every selected recognizer. Received text remains available for saving. Select devices again and start a new session after correcting a fault.

If an audio driver cannot finish stopping, MicaStats stops the meeting and retains its resources until the native operation returns. Save received text and restart MicaStats if audio resources remain unavailable.

## Automatic transcript recovery

Every received transcript update is saved in the background under `%APPDATA%\MicaStats\Meetings`. Each meeting has crash-safe JSON recovery state and a readable Markdown transcript. Earlier meetings remain in that folder; use **Saved transcripts** at the bottom of Meeting Assistant to open it. The adjacent save status shows whether the latest update is saved, pending, or failed.

Automatic recovery includes transcript times, source labels and IDs, dual-recognizer alternatives, and listening gaps. It does not save source audio, configured service addresses, reference notes, AI summaries, or AI answers. **Save Markdown** is a separate manual export and still includes the current AI report.

When Meeting Assistant opens, it restores the latest nonempty transcript in a stopped state. Restoration does not start audio devices, contact an ASR/TTS/AI service, or restart analysis. A confirmed new session clears the workspace but retains the old meeting files. Normal window close and application exit wait for pending transcript writes. If saving fails, the window remains open so you can retry or use **Save Markdown**. A sudden process or system termination can still lose results that have not reached the app or writes that are still pending; no application can guarantee zero loss for every failure.

## Reading the meeting workspace

The transcript uses compact **Microphone / Conference audio** labels and time ranges. Hover a source label for its reference ID; Markdown exports keep the IDs. ASR alternatives and listening gaps remain visible. The view follows incoming text when you are at the bottom. Scroll upward to review earlier text without being moved on each update; use **↓ Latest** to return to the newest text. Text remains selectable and copyable.

In **Summary & answers**, type into **Ask about this meeting** and press Enter or **Ask privately**. A meeting must be active; an in-progress question shows Preparing and blocks duplicate clicks. **Copy answer** copies the visible answer with sources and missing-information notes and confirms success. **Prepare speech** opens the Speech tab with only the answer text; press Speak there to play it. The AI settings shortcut and empty-state guidance explain missing setup or answers. The selected question stays selected after analysis refresh when it is still present.

The **🎤 Microphone** and **🔊 Conference output** cards show the selected device and the last three seconds of sound amplitude. They use the same audio buffers already captured for transcription, with no additional capture device or network request. Expand **🎛 Audio setup** to change devices before starting. Setup collapses after Start so the conversation has more room.

The waveform updates about ten times per second while the window is visible and listening. Its height uses a square-root scale to keep quiet sound visible; **dBFS** is the recent digital peak level (0 is full scale). **Sound detected** means audio energy, not identified speech or a successful transcription. **Quiet** indicates low incoming levels; **No recent audio** means no fresh buffers arrived, which is normal when output loopback is silent. Check the selected output if expected conference sound never appears. **Near clipping** warns of samples near full scale; reduce the source level if audio is distorted.

Silence scrolls to a flat line. TTS pauses, Stop and capture faults clear both graphs and show a text state. The display retains only a small amplitude history in memory, never an audio recording. Minimizing or hiding the window stops display polling while the existing meeting capture continues.

## Running alongside a conference app

Microphone capture, selected-output loopback capture and speech playback all explicitly use WASAPI **shared mode**. Windows supports multiple applications sharing an endpoint in this mode; loopback reads the selected output's audio mix. See Microsoft's [shared-mode contract](https://learn.microsoft.com/en-us/windows/win32/api/audiosessiontypes/ne-audiosessiontypes-audclnt_sharemode) and [loopback recording documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording).

MicaStats does not change Windows default devices, application routing, endpoint volume or mute. Start, Stop and the pause around speech playback operate only on MicaStats' own audio clients. The conference app continues using its own clients. Select the same output device used by the conference app; loopback includes all applications playing through that device, and will not hear a conference routed elsewhere.

This design supports coexistence with Teams, Zoom, Webex and browser calls, but is not a claim of completed live compatibility testing. An endpoint held exclusively by another app or a device/driver interruption can prevent or stop MicaStats capture; it reports a failure instead of taking over the endpoint. Speaker playback of TTS can still be heard by the conference microphone acoustically. Headphones reduce that path; TTS is not injected into the conference microphone stream.

## Manual validation checklist

Automated tests use scripted audio and fake providers. Synthetic HTTP tests verified ASR upload contracts and a generated VoxCPM WAV round trip through ASR2. These checks do not prove hardware capture, language quality, or conference latency.

- With headphones, explicitly start a controlled test using the intended microphone and output. Speak Thai and English, including a sentence crossing a four-second chunk boundary. Check transcript text, source labels and elapsed times.
- Keep the output silent, then play known speech through the selected endpoint. Check that timestamps reflect the later playback time.
- Check that only the expected source waveform responds, quiet output becomes flat, and both graphs clear during speech playback, Stop and device faults. Hide/show the window while listening; display updates should resume without interrupting the meeting capture. Waveforms indicate amplitude, not recognition accuracy.
- Disconnect each selected device in turn. Confirm listening stops visibly and received text remains available; reconnect and explicitly start again.
- Configure the AI provider, ask a supported question and a question absent from the evidence. Check citations and missing-information labels. Exhaust a small test allowance and verify transcription continues.
- Select a test note, ask about it, then remove it. Store harmless test text as a credential while analysis or speech is pending; confirm old derived content is cleared in both Ask and Meeting Assistant.
- Test Speak and Stop speaking during generation and playback, with each intended output device. Confirm the gap marker and capture resumption. Stop the meeting during playback and confirm capture stays stopped. Verify standalone speech and that Start stays disabled until speech finishes.
- Produce transcript updates, confirm the bottom status reaches saved, and inspect the JSON and readable Markdown under `%APPDATA%\MicaStats\Meetings`. Stop and reopen Meeting Assistant; confirm the latest nonempty transcript is restored without capture, network, or AI work. Start a new session and confirm the earlier meeting remains available through **Saved transcripts**. Also use **Save Markdown** and verify that the manual report includes the current AI analysis.
- With ASR2 + ASR1 selected, check matching and conflicting readings, slow/unavailable providers, and the persistent degraded status. Compare against a known transcript before drawing conclusions about recognition accuracy.
- For a Thai conversation, verify Follow transcript and Thai produce Thai analysis; select English and verify subsequent analysis changes language while the transcript stays Thai. Scripted automated tests verify policy and cancellation, not a live model's language quality.

For a controlled coexistence check, repeat the following with Teams, Zoom, Webex, a Chrome call and a Firefox call: confirm bidirectional call audio, select those same devices in MicaStats, start and stop monitoring, and verify the call audio and Windows/app volume, mute and device selections stay unchanged. Use harmless test speech and headphones. Check TTS separately: only MicaStats should show a listening gap while the call continues. Current status for each application: **not live-tested**; automated checks exercise owned-client lifecycle and failure handling. If evaluating exclusive-device conflicts, use a disposable test session rather than an ongoing meeting.

## วิธีใช้ภาษาไทย

ก่อนใช้งาน เปิด **Configure services** หรือ **Settings → Meeting** แล้วกรอก API base URL ของบริการ ASR/TTS ที่ต้องการใช้ รวมส่วนพาธ API เช่น `/v1` จากนั้นบันทึก ค่าเริ่มต้นของทุกบริการเป็นช่องว่าง ไม่มีที่อยู่บริการส่วนตัวฝังในโปรแกรม การเปลี่ยนที่อยู่บริการจะหยุดงานเสียงที่กำลังทำงานก่อนใช้ค่าใหม่

ตั้งค่า ASR หนึ่งบริการสำหรับโหมด ASR1 หรือ ASR2 หรือกรอกทั้งสองบริการแล้วเลือก **ASR2 + ASR1** เพื่อส่งเสียงเดียวกันไปถอดเสียงพร้อมกันทั้งสองแห่ง โหมดเปรียบเทียบส่งข้อมูลเสียงเพิ่มเป็นสองชุด ส่วน TTS จำเป็นเฉพาะเมื่อใช้ Speak ปุ่ม **Test connection** ทดสอบ URL ที่กรอกโดยไม่บันทึกค่า: ASR ใช้เสียงเงียบที่โปรแกรมสร้างขึ้น และ TTS ใช้ข้อความทดสอบคงที่โดยไม่เล่นเสียง ไม่มีการใช้ไมโครโฟนหรือเนื้อหาการประชุม กด **Cancel test** เพื่อยกเลิกได้ และการทดสอบจะหมดเวลาหลัง 20 วินาที

การรับเสียงไมโครโฟน การรับเสียงขาออกแบบ loopback และการเล่นเสียง ใช้ WASAPI แบบ shared mode เพื่อใช้อุปกรณ์ร่วมกับแอปประชุม MicaStats ไม่เปลี่ยนอุปกรณ์เริ่มต้น การเลือกอุปกรณ์ของแอปอื่น ระดับเสียง หรือสถานะ mute และหยุดเฉพาะงานเสียงของตัวเอง เลือกอุปกรณ์ขาออกให้ตรงกับที่แอปประชุมใช้ การทำงานร่วมกับแต่ละแอปและไดรเวอร์ยังต้องทดสอบจริง; หากอุปกรณ์ถูกใช้งานแบบ exclusive โปรแกรมอาจรับเสียงไม่ได้และจะแสดงข้อผิดพลาด

เปิด **Meeting Assistant** จากเมนู MicaStats แล้วเลือกไมโครโฟนและอุปกรณ์เสียงขาออกที่ใช้ฟังการประชุม โปรแกรมจะเริ่มรับเสียงเมื่อกด **Start listening** เท่านั้น เสียงขาออกครอบคลุมทุกแอปที่ใช้อุปกรณ์นั้น โดยค่าเริ่มต้นใช้ ASR2 และเลือก ASR1 หรือ ASR2 + ASR1 ได้ก่อนเริ่ม ข้อความจะทยอยปรากฏหลังส่งเสียงแต่ละช่วงประมาณสี่วินาทีและรอบริการประมวลผล

การ์ด **🎤 Microphone** และ **🔊 Conference output** แสดงชื่ออุปกรณ์และกราฟระดับเสียงย้อนหลังสามวินาทีจากข้อมูลเสียงที่รับจริง กด **🎛 Audio setup** เพื่อเลือกอุปกรณ์ก่อนเริ่ม ค่า **dBFS** คือระดับสูงสุดของสัญญาณดิจิทัล โดย 0 คือเต็มสเกล **Sound detected** หมายถึงมีเสียง ไม่ได้รับรองว่าถอดเสียงสำเร็จ **Quiet** คือเสียงเบา และ **No recent audio** คือยังไม่มีข้อมูลเสียงใหม่ ซึ่งเกิดได้ตามปกติเมื่อเสียงขาออกเงียบ กราฟจะค่อย ๆ ราบเมื่อไม่มีเสียง และล้างพร้อมแสดงสถานะเมื่อหยุดฟัง เกิดข้อผิดพลาด หรือพักระหว่างเล่น TTS ไม่มีการเปิดอุปกรณ์เพิ่มหรือบันทึกไฟล์เสียงเพื่อแสดงกราฟ

ข้อความถอดเสียงแสดงแหล่งเสียงและช่วงเวลาแบบกระชับ โดยเก็บรหัสอ้างอิงไว้ในคำแนะนำเมื่อชี้เมาส์และไฟล์ Markdown เมื่ออยู่ท้ายรายการ โปรแกรมจะตามข้อความใหม่ให้อัตโนมัติ หากเลื่อนกลับไปอ่านก่อนหน้า ตำแหน่งและข้อความที่เลือกจะคงอยู่ กด **↓ Latest** เพื่อกลับไปข้อความล่าสุด ในแท็บ **Summary & answers** พิมพ์คำถามแล้วกด Enter ได้ ระหว่างรอคำตอบจะป้องกันการกดซ้ำ ปุ่ม **Copy answer** คัดลอกคำตอบพร้อมข้อมูลอ้างอิงและแสดงข้อความยืนยัน ส่วน **Prepare speech** เปิดแท็บ Speech โดยยังไม่เล่นเสียงจนกด Speak

เปิด Assistant และตั้งค่าโมเดลที่ **Settings → AI** เพื่อดูสรุป ประเด็นสำคัญ คำถาม และคำตอบที่แนะนำ ในแท็บ **Summary & answers** เลือก **AI language** เป็น **Follow transcript** (ตามภาษาบทสนทนา), **Thai · ไทย** หรือ **English** โปรแกรมจำตัวเลือกไว้ การเปลี่ยนภาษาจะล้างคำตอบและข้อความเตรียมพูดเดิม แล้ววิเคราะห์ใหม่ระหว่างที่การรับเสียงยังทำงาน โดยไม่แปลหรือแก้ข้อความถอดเสียง สามารถพิมพ์คำถามเองและเลือกโน้ต MicaPad ที่ต้องการใช้เป็นข้อมูลอ้างอิงได้ ตรวจสอบแหล่งอ้างอิงและข้อมูลที่ยังขาดก่อนนำคำตอบไปใช้ แต่ละรอบวิเคราะห์หรือคำถามจะใช้โควตา AI ร่วมกับ Ask; เมื่อโควตาหมด การถอดเสียงยังทำงานต่อ

เลือกคำตอบหรือพิมพ์ข้อความ เลือกอุปกรณ์เล่นเสียง แล้วกด **Speak** เพื่อให้ VoxCPM อ่านออกเสียง กด **Stop speaking** เพื่อหยุด ระหว่างเล่นเสียง โปรแกรมจะหยุดฟังทั้งไมโครโฟนและเสียงขาออกชั่วคราวและบันทึกช่วงที่ไม่ได้ฟัง จากนั้นกลับมาฟังเฉพาะเมื่อการประชุมเดิมยังทำงานอยู่ คำพูดในการประชุมระหว่างช่วงนี้จะไม่ถูกถอดเสียง เสียงจากลำโพงอาจเข้าถึงไมโครโฟนของแอปประชุมได้ จึงควรใช้หูฟังเมื่อต้องการฟังเป็นการส่วนตัว

กด **Stop session** เพื่อจบการฟัง โปรแกรมจะบันทึกข้อความถอดเสียงทุกครั้งที่ได้รับข้อมูลใหม่ไว้เบื้องหลังที่ `%APPDATA%\MicaStats\Meetings` โดยแต่ละการประชุมมีไฟล์ JSON สำหรับกู้คืนอย่างปลอดภัยและไฟล์ Markdown ที่อ่านได้ การบันทึกอัตโนมัติเก็บเวลา แหล่งเสียงและรหัส ข้อความทางเลือกจาก ASR สองบริการ และช่วงที่หยุดฟัง แต่ไม่เก็บไฟล์เสียง ที่อยู่บริการ โน้ตอ้างอิง สรุป AI หรือคำตอบ AI ปุ่ม **Save Markdown** ยังใช้ส่งออกรายงานที่รวมข้อมูล AI ปัจจุบันได้

เมื่อเปิด Meeting Assistant โปรแกรมจะคืนข้อความล่าสุดที่ไม่ว่างในสถานะหยุด โดยไม่เริ่มใช้อุปกรณ์ ไม่เชื่อมต่อเครือข่าย และไม่เริ่มงาน AI แถบด้านล่างแสดงสถานะการบันทึก และ **Saved transcripts** เปิดโฟลเดอร์ที่เก็บการประชุมก่อนหน้า การเริ่ม session ใหม่จะขอคำยืนยันและยังเก็บไฟล์เดิมไว้ การปิดหน้าต่างหรือออกจากโปรแกรมตามปกติจะรอเขียนข้อมูลที่ค้างอยู่ หากบันทึกล้มเหลว หน้าต่างจะยังเปิดเพื่อให้ลองใหม่หรือใช้ **Save Markdown** อย่างไรก็ตาม การปิดโปรเซสหรือระบบกะทันหันอาจทำให้ผลลัพธ์ที่ยังมาไม่ถึงโปรแกรมหรือการเขียนที่ยังค้างอยู่สูญหาย จึงไม่สามารถรับประกันว่าจะไม่สูญเสียข้อมูลในทุกกรณี การลบโน้ตอ้างอิงหรือเก็บข้อความเป็น credential จะล้างคำตอบที่อาศัยข้อมูลเดิมและยกเลิกเสียงที่เกี่ยวข้อง
