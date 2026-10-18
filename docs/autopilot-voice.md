# Plan: talk to Margo (WebRTC voice, her own voice)

**Status: proposal, 18 October 2026. Nothing here is built.** It extends
[Margo as a Foundry autopilot](autopilot.md) and fits the
[one-container-per-instance plan](autopilot-instance-containers.md). Product facts come from a
web-search review on 18 October 2026. Items marked **verify** could not be confirmed against the
official page and must be checked before building.

## What you get

You open Margo's voice page (browser first, phone later), sign in once, and talk. She answers in
her own voice with low latency, knows the same things, follows the same skills and has the same
limits as Margo in Teams. Voice is another way into the same Margo, not a second assistant.

## Requirements

1. **Same Margo.** The same persona file, skills, guard, state tool and `parity.py` list. Voice adds
   only a short spoken-style note (short sentences, no tables, times read aloud); the persona
   file is unchanged.
2. **Same guard.** The voice model gets only Margo's function tools. Every call runs on the
   server, through the guard and `margo-state`. No MCP servers or agent bindings attached to the
   voice session, because those would run outside the guard.
3. **Only the manager.** The page signs in with Entra (MSAL). The server checks the token's
   tenant, audience and object ID, and the object ID must be the instance's manager. That
   sign-in also provides the user assertion for on-behalf-of reads, which helps with design
   verification item 1.
4. **No approval by voice alone.** Margo can propose by voice. The exact action is approved on a
   card that shows the payload, in the page or in Teams. A spoken "yes" can be misheard, said by
   someone else in the room, or replayed, and AGENTS.md ties approval to one exact action,
   payload and revision.
5. **Low latency.** Target under about 800 ms to first audio for conversation. A Work IQ lookup
   takes seconds, so she says a short progress line ("checking your calendar") while it runs.
6. **Her own voice**, as a custom voice under Microsoft's Limited Access rules, with an interim
   built-in voice until it is approved.
7. **Privacy and disclosure.** No audio is stored. Transcripts, if kept, are private state with a
   retention period. Every session opens by saying Margo is an AI voice.

## Options

| | Architecture | Latency | Custom voice | Tools under our control | Status |
| --- | --- | --- | --- | --- | --- |
| **A. Voice Live** (Azure) | Managed speech-to-speech (gpt-realtime-2.1, or Microsoft's own realtime model) or a cascade, behind one API | ~300–700 ms typical for speech-to-speech | **Yes**: Azure custom voice (`azure-custom`) on the same Foundry resource | Yes: function tools declared in the session, run by our server | GA over WebSocket; **WebRTC in preview**, removed from the GA Python SDK; no ephemeral browser token, so a server broker is needed |
| B. Azure OpenAI Realtime | gpt-realtime-2.1 over WebRTC, browser connects with an ephemeral key minted by our server | Lowest: WebRTC transport ~50–100 ms | **No**: built-in voices only | Tool calls arrive in the browser's data channel; a server sideband is reportedly unsupported on Azure (**verify**) | GA |
| C. Cascade on Margo's own engine | Streaming speech-to-text → today's text engine and tools → streaming text-to-speech with the custom voice | ~700–1200 ms | Yes | Yes: the existing engine, unchanged | Building blocks GA |
| D. Non-Azure TTS (ElevenLabs, Cartesia) | Cascade with a third-party voice | Fast TTS (vendor claims ~75–90 ms model time) | Yes: easy cloning | Yes | Audio and spoken Microsoft 365 content leave the Microsoft boundary: a new subprocessor and a privacy assessment, unless self-hosted (Cartesia offers self-hosting under an enterprise contract) |

## Recommendation

**Voice Live, with Margo's server in the middle, in Margo's own Foundry resource and region.**

- **Fast path:** speech-to-speech with gpt-realtime-2.1, or Microsoft's own realtime model if it
  measures faster in your region. That's your fast voice model.
- **Tool-heavy turns:** the same Voice Live session can use a stronger text model (a cascade),
  so briefs and lookups stay as reliable as in Teams.
- **Why it fits:** it is the only Azure-native option found that combines speech-to-speech
  latency, a custom voice, and processing inside the Microsoft boundary.

How the pieces connect:

```text
Browser (MSAL sign-in, mic) ──WebRTC audio──► Margo voice relay (in the instance container)
                                                 │  checks the manager's token
                                                 │  holds the Voice Live session (managed identity)
                                                 │  executes tool calls through the guard
                                                 └──WebSocket (GA)──► Voice Live (model + custom voice)
```

Today the relay takes the browser's WebRTC audio and talks to Voice Live over its GA WebSocket.
That costs one extra hop, but the server owns the session, the instructions and every tool call.

When Voice Live's WebRTC path is GA, the browser's audio can go straight to Voice Live while the
server keeps the control channel, if verification shows tool calls arrive on that server-held
channel. That removes the hop.

## Her own voice

1. **Choose the voice.** Either commission a voice talent (a professional voice actor) whose
   recordings become Margo's voice, or pick an Azure HD voice as her permanent voice.
   A real person's voice may only be used with that person's recorded consent; no imitation of
   anyone else.
2. **Apply for Limited Access** for professional custom voice. It is granted to customers
   managed by Microsoft account teams, for registered use cases (**verify** eligibility for this
   tenant).
3. **Record.** The talent records a consent statement, which Microsoft checks against the
   training audio, plus 300–2000 utterances (about 30 minutes to 3 hours of audio).
4. **Train and deploy** in the Foundry portal (about 10 compute hours on average) to the same
   Foundry resource as Voice Live. Then switch the session voice to `azure-custom` with the
   deployment's endpoint ID.
5. **Until approved,** use an Azure HD voice so everything else can ship.

Personal voice (the faster, lighter option) is a poor fit: its approved uses exclude reading
open-ended generated content, which is all Margo does.

## Phases

| Phase | Outcome | Exit evidence |
| --- | --- | --- |
| **V0. Verify** | Answers to the list below; measured p50/p95 latency in your region | Written results; recordings use fictional content only |
| **V1. Browser voice** | Sign in and talk to Margo in an HD voice; read-only, same tools and guard | Guard and parity tests pass for voice turns; spoken disclosure at session start |
| **V2. Her voice** | Custom voice live after Limited Access approval and training | Consent records on file; voice deployed in the same resource |
| **V3. Direct WebRTC** | Relay hop removed when Voice Live WebRTC is GA | Same tests; lower measured latency |
| **V4. Phone** (optional) | An Azure Communication Services number reaches Margo | A step-up check (caller ID can be spoofed) before any private data is spoken |

## Verification list

1. Does Voice Live accept a managed-identity (Entra) connection from the server, in your region,
   with gpt-realtime-2.1 and a custom voice together?
2. On Voice Live WebRTC, do function calls arrive on a channel the server holds, or only in the
   browser?
3. Do noise suppression, echo cancellation and semantic turn detection apply on the WebRTC path?
4. Where is audio processed under global routing, and can it be pinned to a data zone?
5. Measured latency (p50/p95, first audio and turn-taking) for speech-to-speech versus a cascade.
6. Pricing per minute for the chosen tier and the custom-voice audio rate. No estimate is made
   here.

## Legal and responsible-use duties

- **Disclose the synthetic voice** at the start of every session, and give a way to report misuse
  (Microsoft's text-to-speech code of conduct).
- **EU AI Act Article 50** transparency duties for AI systems that talk to people (dates per a
  law-firm summary; **verify** with counsel).
- **Recording consent:** some jurisdictions require every party's consent. Don't record audio;
  keep transcripts minimal, private and outside the repository.
- **Outbound calls** with an AI voice need prior consent in the US (FCC, under the TCPA). Margo
  does not place calls in this plan.

## Decisions needed

1. Whose voice: commission a voice talent for a custom voice, or choose an Azure HD voice.
2. Browser only first, or phone as well.
3. Keep text transcripts of voice sessions (and for how long), or keep nothing.

## Sources

- Voice Live overview: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live
- Voice Live custom voices: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to-customize
- Voice Live WebRTC (preview): https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-webrtc
- Voice Live Python SDK release notes: https://pypi.org/project/azure-ai-voicelive/
- Azure OpenAI Realtime over WebRTC: https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-webrtc
- Professional custom voice training: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/professional-voice-train-voice
- Custom voice Limited Access: https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/speech-service/text-to-speech/limited-access
- Voice talent consent: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/how-to-custom-voice-talent
- Personal voice: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/personal-voice-overview
- Text-to-speech code of conduct: https://learn.microsoft.com/en-us/legal/cognitive-services/speech-service/text-to-speech/code-of-conduct
- Voice Live telephony: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-telephony
- FCC ruling on AI voices: https://www.fcc.gov/document/fcc-makes-ai-generated-voices-robocalls-illegal
