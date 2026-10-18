"""Offline tests for voice: the same Margo, the same tools and guard, nothing approved by voice."""

import asyncio
import base64
import importlib
import json
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))
autopilot = importlib.import_module("margo_autopilot")
engine_mod = importlib.import_module("margo_autopilot.engine")
skills_mod = importlib.import_module("margo_autopilot.skills")
voice = importlib.import_module("margo_autopilot.voice.session")

SETTINGS = voice.VoiceSettings(model="fictional-realtime", max_tool_calls=3)
NOW = 1_800_000_000


class SessionConfiguration(unittest.TestCase):
    def test_same_persona_skills_and_tools_plus_speaking_note(self):
        library = skills_mod.SkillLibrary(ROOT)
        instructions = engine_mod.load_instructions(ROOT, library=library,
                                                    now=datetime(2026, 10, 18, 9, tzinfo=timezone.utc))
        event = voice.session_update(instructions + "\n\n" + voice.VOICE_ADDENDUM,
                                     engine_mod.function_tools(autopilot.ATTENDED), SETTINGS)
        session = event["session"]
        self.assertEqual(event["type"], "session.update")
        self.assertTrue(session["instructions"].startswith(library.persona()))
        self.assertIn("- **decision-log**:", session["instructions"])
        self.assertIn("Nothing is approved or sent by voice", session["instructions"])
        names = {tool["name"] for tool in session["tools"]}
        self.assertTrue({"workiq-fetch", "margo-state", "load_skill", "office_requests"} <= names)
        self.assertFalse(names & {"workiq-do_action", "workiq-create_entity", "workiq-delete_entity"})
        self.assertTrue(all(tool["type"] == "function" for tool in session["tools"]))
        self.assertNotIn("mcp", json.dumps(session).lower())
        self.assertEqual(session["turn_detection"]["type"], "azure_semantic_vad")
        self.assertEqual(session["input_audio_echo_cancellation"]["type"], "server_echo_cancellation")

    def test_voice_choice_custom_or_standard(self):
        self.assertEqual(SETTINGS.voice(), {"type": "azure-standard", "name": voice.DEFAULT_VOICE})
        custom = voice.VoiceSettings.from_env({"VOICE_LIVE_MODEL": "m", "MARGO_VOICE_NAME": "MargoNeural",
                                               "MARGO_VOICE_ENDPOINT_ID": "fictional-voice-deployment"})
        self.assertEqual(custom.voice()["type"], "azure-custom")
        self.assertEqual(custom.voice()["endpoint_id"], "fictional-voice-deployment")
        with self.assertRaises(ValueError):
            voice.VoiceSettings.from_env({})


class Bridge(unittest.TestCase):
    def setUp(self):
        self.up, self.down, self.calls, self.locked = [], [], [], []

        async def up(event):
            self.up.append(event)

        async def down(message):
            self.down.append(message)

        async def dispatch(name, raw):
            self.calls.append((name, raw))
            return {"status": "ok", "tool": name}

        async def run_locked(work):
            self.locked.append(True)
            return await work()

        self.bridge = voice.VoiceBridge(dispatch=dispatch, upstream=up, downstream=down, settings=SETTINGS,
                                        clip=engine_mod._clip, run_locked=run_locked)

    def run_events(self, *events):
        async def go():
            for event in events:
                await self.bridge.from_service(event)
        asyncio.run(go())

    def client(self, message):
        return asyncio.run(self.bridge.from_client(message))

    def test_audio_flows_only_after_ready_and_barge_in_interrupts(self):
        audio = base64.b64encode(b"\x00\x01" * 480).decode()
        self.client({"type": "audio", "audio": audio})
        self.assertEqual(self.up, [])  # nothing is sent before the session is configured
        self.run_events({"type": "session.updated"})
        self.assertEqual(self.down[0]["type"], "ready")
        self.assertIn("synthetic", self.down[0]["disclosure"])
        self.client({"type": "audio", "audio": audio})
        self.assertEqual(self.up[-1], {"type": "input_audio_buffer.append", "audio": audio})
        for bad in ("not base64!", "A" * (voice.MAX_AUDIO_CHARS + 4), 42, None):
            self.client({"type": "audio", "audio": bad})
        self.assertEqual(len(self.up), 1)
        self.run_events({"type": "response.audio.delta", "delta": "AAAA"}, {"type": "input_audio_buffer.speech_started"},
                        {"type": "conversation.item.input_audio_transcription.completed", "transcript": " Brief me "},
                        {"type": "response.audio_transcript.done", "transcript": "Here is your morning."})
        self.assertEqual([m["type"] for m in self.down[1:]], ["audio", "interrupt", "caption", "caption"])
        self.assertEqual(self.down[3], {"type": "caption", "who": "you", "text": "Brief me"})
        self.assertEqual(self.client({"type": "stop"}), "stop")
        self.assertIsNone(self.client({"type": "unknown"}))

    def test_tool_calls_run_through_dispatch_then_one_response(self):
        self.run_events(
            {"type": "response.function_call_arguments.done", "call_id": "c1", "name": "workiq-fetch",
             "arguments": json.dumps({"on_data_of": "principal", "entityUrls": ["/me/messages"]})},
            {"type": "response.function_call_arguments.done", "call_id": "c2", "name": "margo-state",
             "arguments": json.dumps({"script": "work_state.py", "args": ["list"]})},
            {"type": "response.done"})
        self.assertEqual([name for name, _ in self.calls], ["workiq-fetch", "margo-state"])
        self.assertEqual(len(self.locked), 1)  # only the state call takes the state lease
        outputs = [event for event in self.up if event["type"] == "conversation.item.create"]
        self.assertEqual([event["item"]["call_id"] for event in outputs], ["c1", "c2"])
        self.assertEqual(json.loads(outputs[0]["item"]["output"])["status"], "ok")
        self.assertEqual([event["type"] for event in self.up].count("response.create"), 1)
        self.assertEqual(self.down[-1], {"type": "status", "text": ""})
        self.run_events({"type": "response.done"})
        self.assertEqual([event["type"] for event in self.up].count("response.create"), 1)

    def test_tool_budget_and_malformed_calls(self):
        for index in range(5):
            self.run_events({"type": "response.function_call_arguments.done", "call_id": "c%d" % index,
                             "name": "load_skill", "arguments": "{}"})
        self.assertEqual(len(self.calls), SETTINGS.max_tool_calls)
        last = json.loads(self.up[-1]["item"]["output"])
        self.assertEqual(last["status"], "budget_exhausted")
        before = len(self.up)
        self.run_events({"type": "response.function_call_arguments.done", "name": "load_skill"})
        self.assertEqual(len(self.up), before)  # no call ID: nothing to answer

    def test_service_errors_are_reported_without_detail(self):
        self.run_events({"type": "error", "error": {"message": "internal detail with secrets"}})
        self.assertEqual(self.down[-1]["type"], "error")
        self.assertNotIn("secret", json.dumps(self.down))


class Claims(unittest.TestCase):
    TENANT, CLIENT = "fictional-tenant", "fictional-blueprint-app"

    def claims(self, **overrides):
        value = {"tid": self.TENANT, "aud": self.CLIENT, "scp": "Voice.Talk", "exp": NOW + 600, "oid": "principal-1"}
        value.update(overrides)
        return value

    def check(self, claims):
        return voice.check_claims(claims, tenant_id=self.TENANT, client_id=self.CLIENT, scope="Voice.Talk", now=NOW)

    def test_only_a_person_in_this_tenant_with_the_voice_scope(self):
        self.assertEqual(self.check(self.claims()), "principal-1")
        self.assertEqual(self.check(self.claims(aud="api://" + self.CLIENT, scp="openid Voice.Talk")), "principal-1")
        for bad in (self.claims(tid="other"), self.claims(aud="other-app"), self.claims(scp="Mail.Read"),
                    self.claims(scp="Voice.TalkMore"), self.claims(exp=NOW - 1), self.claims(oid=""),
                    self.claims(idtyp="app"), None):
            with self.subTest(claims=bad), self.assertRaises(voice.TokenRejected):
                self.check(bad)


if __name__ == "__main__":
    unittest.main()
