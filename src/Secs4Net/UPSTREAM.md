# Secs4Net — HSMS-SS revision for ALE_SecsGem

Source: [mkjeff/secs4net](https://github.com/mkjeff/secs4net/tree/6f540a7540fc7a531d1d526c04375f4b05a7641f/src/Secs4Net),
commit `6f540a7540fc7a531d1d526c04375f4b05a7641f`, the commit recorded in the official Secs4Net 3.1.0
NuGet package. The MIT license is retained in `LICENSE`. Only `src/Secs4Net` is imported; the first
commit that adds this folder holds it unmodified, so `git diff` against that commit shows every local change.

This is not an official Secs4Net release. The package keeps the `Secs4Net` id and assembly version
3.1.0.0 so that Secs4Net.Json and Secs4Net.Sml bind to it, and is versioned 3.1.0.1 so NuGet never
resolves the unmodified 3.1.0 in its place. `./pack-local-nuget.ps1` publishes it beside ALE_SecsGem.

## Why

ALE_SecsGem must conform to SEMI E37 (HSMS) and E37.1 (HSMS-SS). Upstream 3.1.0 does not:

| Upstream 3.1.0 | Requirement |
|---|---|
| T8 never enforced (timer calls commented out in `PipeDecoder`) | E37 §9.2.3.1; E37.1 Tables 1/2 |
| Select.req in SELECTED answered with status 0 | E37.1 §7.1.1, §7.7 |
| Data and Linktest.req accepted in NOT SELECTED | E37.1 Table 1 #4, §7.4 |
| PType, SType, Session ID and control length never checked; control message with text decoded as data | E37 §8.3, §9.3.3.1; E37.1 §7.5, §8.1–§8.3 |
| Deselect.req ignored | E37.1 §7.3, §7.7 |
| Non-zero Select.rsp only logged; reconnect after a drop without T5 | E37.1 Table 2 #4; E37 §9.2.1.1 |

`ISecsConnection`'s transport members are `internal`, so a conforming connection cannot be supplied
from outside the assembly; the change has to live here.

## What changed

Protocol behavior (`HsmsConnection.cs`, `PipeDecoder.cs`):

- One TCP/IP connection at a time, run by a single loop; every message of a connection is handled in
  arrival order, control messages inline before the next message is read. A data message right behind
  a Select is therefore never seen as NOT SELECTED.
- Reject is not supported (E37.1 §5.1). Every case that would call for it and every HSMS-SS
  restriction violation closes the TCP/IP connection (E37.1 §7.5, §7.7): PType ≠ 0, an SType not
  defined by HSMS, Message Length < 10, a control message with text, a control Session ID other than
  0xFFFF, non-zero control header bytes, a data Session ID with its high-order bit set, a data message
  or Linktest.req in NOT SELECTED, Select.req in SELECTED or at the active entity, Deselect.req/.rsp, a
  response with no open transaction of its kind, and a received Reject.req.
- T8 bounds every read inside a message; T7 closes a connection not SELECTED in time; T6 closes on an
  unanswered Select.req or Linktest.req. Separate.req received closes; disposal in SELECTED sends
  Separate.req first.
- Active mode: non-zero SelectStatus closes the connection, and every connect after the first waits T5.
- Passive mode: the listener is not accepted from while a connection is open (E37 §9.2.4.1.1, third option).
- Messages are framed by Message Length; the text is decoded once complete and must fill the length exactly.
- A frame write that fails part way closes the connection so a later frame cannot follow the fragment.

Other:

- `SecsGem`: the S9F1 guard for an unrecognized Device ID now exempts only S9F1 itself (upstream
  exempted every message with F = 1 or S = 9).
- `PipeDecoder` is internal; `ISecsConnection.GetDataMessages` yields `DecodedMessage`. The public
  `HsmsConnection` / `SecsGem` API is unchanged. Header checks read the raw bytes that 3.1.0's
  `MessageHeader` (an inline array) holds.

Item encoding, `SecsMessage`, `SecsGemOptions` and the reply-matching in `SecsGem` remain upstream code.

## Updating upstream

Diff the new upstream `src/Secs4Net` against the import commit. Re-apply upstream changes to files
not listed above directly; for `HsmsConnection.cs`, `PipeDecoder.cs`, `ISecsConnection.cs` and
`SecsGem.cs`, merge them by hand against the behavior described here, then bump `<Version>`.
