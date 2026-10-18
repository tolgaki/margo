"""Activity classification that needs no SDK import, so it can be tested offline."""

import hashlib
import json


def _value(source, name):
    if isinstance(source, dict):
        lowered = {str(key).lower(): value for key, value in source.items()}
        return source.get(name) or lowered.get(name.lower()) or ""
    return getattr(source, name, None) or ""


def is_email(activity):
    """Email reaches the message route too: channel ``email`` or ``agents:email``, or product ``email``.

    Mirrors the classification in Microsoft's Foundry autopilot sample. Treating email as a chat
    turn would let mail trigger on-behalf-of reads and replies, which this version never does.
    """
    channel_id = getattr(activity, "channel_id", None)
    channel = str(getattr(channel_id, "channel", "") or "")
    sub_channel = str(getattr(channel_id, "sub_channel", "") or "")
    if not channel:
        channel, _, sub_channel = str(channel_id or "").partition(":")
    channel, sub_channel = channel.lower(), sub_channel.lower()
    if channel == "email" or (channel == "agents" and sub_channel == "email"):
        return True
    getter = getattr(activity, "get_product_info_entity", None)
    product = getter() if callable(getter) else None
    if str(_value(product, "id")).lower() == "email":
        return True
    for entity in getattr(activity, "entities", None) or []:
        if str(_value(entity, "type")).lower() == "productinfo" and str(_value(entity, "id")).lower() == "email":
            return True
    return False


def stable_ids(message_id, conversation_id, sender_id, text):
    """Return ``(external_id, conversation_id)`` for an inbound message or mail notification.

    The office queue records one request per external ID. A missing ID must not become a shared
    placeholder, which would make every later message collide with the first. It is derived
    from the content instead: a redelivery is recorded once, and different messages never share
    an ID (identical repeats from the same sender in the same conversation are one request).
    """
    if not isinstance(message_id, str) or not message_id:
        material = json.dumps([sender_id or None, conversation_id or None, text or ""], ensure_ascii=False)
        message_id = "sha256-" + hashlib.sha256(material.encode("utf-8")).hexdigest()
    if not isinstance(conversation_id, str) or not conversation_id:
        conversation_id = "message-" + message_id
    return message_id, conversation_id
