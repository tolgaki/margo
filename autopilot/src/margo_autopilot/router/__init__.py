"""The shared front door for per-instance hosting: decides where each activity goes.

``core`` is standard library only and tested offline. ``app`` (aiohttp, Agents SDK, Azure) is
imported only by the running service.
"""
