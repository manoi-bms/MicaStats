# Meeting Assistant

Open **Meeting Assistant** from the MicaStats menu. Nothing is recorded until you choose **Start listening**.

First open **Configure services** or **Settings → Meeting**. Enter your compatible ASR and/or TTS API base URLs, including their API path, and save. All three addresses start empty. For example, `https://asr.example/v1` is a reserved-domain illustration, not a working service. Configure only the services you intend to use; transcription and speech have separate readiness checks. Saving new service addresses stops active voice work before applying them.

**Only one ASR endpoint is needed.** ASR2 (Qwen) and ASR1 (Typhoon) are alternative request profiles, not a primary/backup pair. Configure the matching profile and select it in Meeting Assistant; audio goes only to the selected service, with no automatic fallback. TTS is separate and optional for the Speak feature.

Each service has a **Test connection** button that checks the address currently entered, before you save. ASR sends one second of generated silence and validates the transcription response; TTS sends the fixed phrase “Connection test.” and validates the returned WAV without playing it. No microphone audio, meeting content or notes are used. Inline results include elapsed time; **Cancel test**, editing the address or closing Settings cancels the request. Tests time out after 20 seconds, never save settings, and do not prove recognition quality or hardware audio operation.

| Setting | Compatible service | Routes appended to the base URL |
| --- | --- | --- |
| ASR2 | Qwen ASR, model `Qwen/Qwen3-ASR-1.7B` | `audio/transcriptions` |
| ASR1 | Typhoon ASR, model `typhoon-asr-realtime` | `audio/transcriptions` |
| TTS | VoxCPM, model `voxcpm-thai` | `audio/speech`, `voices` |

1. Choose your microphone and the output device that plays conference audio. The output source includes all applications playing on that device. Source labels identify the microphone and output, not individual attendees.
2. Keep **ASR2** selected for the preferred service, or choose ASR1 before starting. Each source uploads four-second WAV chunks. Text appears after the service finishes each chunk; this is near-real-time transcription, not a subsecond stream.
3. Enable the Assistant and configure a model in **Settings → AI** to receive a summary, key points, detected questions and private suggested answers. You can also type a question. Sources identify the transcript segments and selected note passages supporting an answer; uncited content is marked unsupported.
4. Select only the MicaPad reference notes you want to share with the configured AI provider. Removing a note clears derived answers. Storing text as a credential also clears retained note context and speech; selected notes must be read again through credential filtering.
5. Choose an answer for speech or enter text in the speech panel. Choose a playback device and press **Speak**. **Refresh voices** fetches the available presets explicitly. No voice samples are enrolled. **Stop speaking** cancels generation or playback.
6. Use **Stop session** to finish listening, and **Save Markdown** to keep the transcript and current analysis. Starting a new session clears the previous transcript. Closing the window discards unsaved content.

ASR and TTS use only the service addresses you configure locally. The app keeps captured and generated audio in memory and does not write it to its logs or configuration. Only an explicit Markdown save persists meeting content locally. These controls do not establish how your chosen services retain requests.

Automatic analysis runs at most once every ten seconds when new transcript text exists. Each analysis or typed question uses one request from the shared daily AI allowance, including failed or cancelled attempts. Continuous analysis can use up to 360 requests per hour; the default daily allowance is 100. At the limit, analysis pauses while transcription continues. Change the allowance explicitly in Settings if needed. ASR and TTS do not consume the chat allowance.

Speech is generated while listening continues. Immediately before playback, MicaStats pauses both capture sources. It records a listening gap and resumes after playback and a short settling interval if the same meeting remains active. Conference speech during that gap is missed. Stopping the meeting prevents capture from resuming. Local speaker playback can also reach the conference application's microphone; headphones help. Speech is played locally, with no automatic reply or virtual microphone injection.

Speech input is limited to 4,096 characters and a single request/playback at a time. Generated audio is limited to 10 MiB and 90 seconds. Meetings stop visibly at eight hours, two million transcript characters, a full transcription queue, or a device/provider failure. Received text remains available for saving. Select devices again and start a new session after correcting a fault.

If an audio driver cannot finish stopping, MicaStats stops the meeting and retains its resources until the native operation returns. Save received text and restart MicaStats if audio resources remain unavailable.

## Running alongside a conference app

Microphone capture, selected-output loopback capture and speech playback all explicitly use WASAPI **shared mode**. Windows supports multiple applications sharing an endpoint in this mode; loopback reads the selected output's audio mix. See Microsoft's [shared-mode contract](https://learn.microsoft.com/en-us/windows/win32/api/audiosessiontypes/ne-audiosessiontypes-audclnt_sharemode) and [loopback recording documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording).

MicaStats does not change Windows default devices, application routing, endpoint volume or mute. Start, Stop and the pause around speech playback operate only on MicaStats' own audio clients. The conference app continues using its own clients. Select the same output device used by the conference app; loopback includes all applications playing through that device, and will not hear a conference routed elsewhere.

This design supports coexistence with Teams, Zoom, Webex and browser calls, but is not a claim of completed live compatibility testing. An endpoint held exclusively by another app or a device/driver interruption can prevent or stop MicaStats capture; it reports a failure instead of taking over the endpoint. Speaker playback of TTS can still be heard by the conference microphone acoustically. Headphones reduce that path; TTS is not injected into the conference microphone stream.

## Manual validation checklist

Automated tests use scripted audio and fake providers. Synthetic HTTP tests verified ASR upload contracts and a generated VoxCPM WAV round trip through ASR2. These checks do not prove hardware capture, language quality, or conference latency.

- With headphones, explicitly start a controlled test using the intended microphone and output. Speak Thai and English, including a sentence crossing a four-second chunk boundary. Check transcript text, source labels and elapsed times.
- Keep the output silent, then play known speech through the selected endpoint. Check that timestamps reflect the later playback time.
- Disconnect each selected device in turn. Confirm listening stops visibly and received text remains available; reconnect and explicitly start again.
- Configure the AI provider, ask a supported question and a question absent from the evidence. Check citations and missing-information labels. Exhaust a small test allowance and verify transcription continues.
- Select a test note, ask about it, then remove it. Store harmless test text as a credential while analysis or speech is pending; confirm old derived content is cleared in both Ask and Meeting Assistant.
- Test Speak and Stop speaking during generation and playback, with each intended output device. Confirm the gap marker and capture resumption. Stop the meeting during playback and confirm capture stays stopped. Verify standalone speech and that Start stays disabled until speech finishes.
- Save Markdown to a chosen location and inspect its transcript, source IDs, analysis and gap markers. Close/reopen the window and confirm content is not restored.

For a controlled coexistence check, repeat the following with Teams, Zoom, Webex, a Chrome call and a Firefox call: confirm bidirectional call audio, select those same devices in MicaStats, start and stop monitoring, and verify the call audio and Windows/app volume, mute and device selections stay unchanged. Use harmless test speech and headphones. Check TTS separately: only MicaStats should show a listening gap while the call continues. Current status for each application: **not live-tested**; automated checks exercise owned-client lifecycle and failure handling. If evaluating exclusive-device conflicts, use a disposable test session rather than an ongoing meeting.

## วิธีใช้ภาษาไทย

ก่อนใช้งาน เปิด **Configure services** หรือ **Settings → Meeting** แล้วกรอก API base URL ของบริการ ASR/TTS ที่ต้องการใช้ รวมส่วนพาธ API เช่น `/v1` จากนั้นบันทึก ค่าเริ่มต้นของทุกบริการเป็นช่องว่าง ไม่มีที่อยู่บริการส่วนตัวฝังในโปรแกรม การเปลี่ยนที่อยู่บริการจะหยุดงานเสียงที่กำลังทำงานก่อนใช้ค่าใหม่

ตั้งค่า ASR เพียงหนึ่งบริการก็เพียงพอ: ASR1 และ ASR2 เป็นตัวเลือกแทนกัน ไม่ใช่บริการหลักและสำรองอัตโนมัติ ส่วน TTS จำเป็นเฉพาะเมื่อใช้ Speak ปุ่ม **Test connection** ทดสอบ URL ที่กรอกโดยไม่บันทึกค่า: ASR ใช้เสียงเงียบที่โปรแกรมสร้างขึ้น และ TTS ใช้ข้อความทดสอบคงที่โดยไม่เล่นเสียง ไม่มีการใช้ไมโครโฟนหรือเนื้อหาการประชุม กด **Cancel test** เพื่อยกเลิกได้ และการทดสอบจะหมดเวลาหลัง 20 วินาที

การรับเสียงไมโครโฟน การรับเสียงขาออกแบบ loopback และการเล่นเสียง ใช้ WASAPI แบบ shared mode เพื่อใช้อุปกรณ์ร่วมกับแอปประชุม MicaStats ไม่เปลี่ยนอุปกรณ์เริ่มต้น การเลือกอุปกรณ์ของแอปอื่น ระดับเสียง หรือสถานะ mute และหยุดเฉพาะงานเสียงของตัวเอง เลือกอุปกรณ์ขาออกให้ตรงกับที่แอปประชุมใช้ การทำงานร่วมกับแต่ละแอปและไดรเวอร์ยังต้องทดสอบจริง; หากอุปกรณ์ถูกใช้งานแบบ exclusive โปรแกรมอาจรับเสียงไม่ได้และจะแสดงข้อผิดพลาด

เปิด **Meeting Assistant** จากเมนู MicaStats แล้วเลือกไมโครโฟนและอุปกรณ์เสียงขาออกที่ใช้ฟังการประชุม โปรแกรมจะเริ่มรับเสียงเมื่อกด **Start listening** เท่านั้น เสียงขาออกครอบคลุมทุกแอปที่ใช้อุปกรณ์นั้น โดยค่าเริ่มต้นใช้ ASR2 และเลือก ASR1 ได้ก่อนเริ่ม ข้อความจะทยอยปรากฏหลังส่งเสียงแต่ละช่วงประมาณสี่วินาทีและรอบริการประมวลผล

เปิด Assistant และตั้งค่าโมเดลที่ **Settings → AI** เพื่อดูสรุป ประเด็นสำคัญ คำถาม และคำตอบที่แนะนำ สามารถพิมพ์คำถามเองและเลือกโน้ต MicaPad ที่ต้องการใช้เป็นข้อมูลอ้างอิงได้ ตรวจสอบแหล่งอ้างอิงและข้อมูลที่ยังขาดก่อนนำคำตอบไปใช้ แต่ละรอบวิเคราะห์หรือคำถามจะใช้โควตา AI ร่วมกับ Ask; เมื่อโควตาหมด การถอดเสียงยังทำงานต่อ

เลือกคำตอบหรือพิมพ์ข้อความ เลือกอุปกรณ์เล่นเสียง แล้วกด **Speak** เพื่อให้ VoxCPM อ่านออกเสียง กด **Stop speaking** เพื่อหยุด ระหว่างเล่นเสียง โปรแกรมจะหยุดฟังทั้งไมโครโฟนและเสียงขาออกชั่วคราวและบันทึกช่วงที่ไม่ได้ฟัง จากนั้นกลับมาฟังเฉพาะเมื่อการประชุมเดิมยังทำงานอยู่ คำพูดในการประชุมระหว่างช่วงนี้จะไม่ถูกถอดเสียง เสียงจากลำโพงอาจเข้าถึงไมโครโฟนของแอปประชุมได้ จึงควรใช้หูฟังเมื่อต้องการฟังเป็นการส่วนตัว

กด **Stop session** เพื่อจบการฟัง และ **Save Markdown** เพื่อเลือกบันทึกข้อความด้วยตนเอง โปรแกรมไม่บันทึกไฟล์เสียงหรือคืนข้อความการประชุมเมื่อเปิดใหม่ การลบโน้ตอ้างอิงหรือเก็บข้อความเป็น credential จะล้างคำตอบที่อาศัยข้อมูลเดิมและยกเลิกเสียงที่เกี่ยวข้อง
