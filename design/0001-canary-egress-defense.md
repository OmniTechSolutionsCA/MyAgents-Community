# RFC 0001 — Canary & Egress Defense against Malicious-Model Data Exfiltration

- **Status:** Implemented — Phase 1 shipped on `feat/canary-egress-defense` (§10), threat **T5**
  closed on `feat/canary-cross-turn-detection` (§11), `design/` tracked in git
- **Target:** `MyAgents.Core` (contracts) + `MyAgents.Agents` (feature wiring)
- **Branch:** `feat/canary-egress-defense`
- **Location:** `design/` — tracked in git. It sits outside `docs-src/docfx.json`'s content globs, so
  it is versioned and reviewable but is not published to the documentation site.
- **Supersedes:** the chat-level sketch of 2026-11-06 (two of its claims were wrong on inspection — see §3)

---

## 1. Problem

An agent that composes untrusted content (web pages, RAG chunks, MCP tool results, subagent
output) can be steered by that content. The framework today protects *inbound* safety
(`IGuardrailPolicy` post-output checks) and *human oversight*
(`IToolApprovalGate` / `IToolApprovalPolicy`), but it has **no mechanism that detects
secret material leaving the process** — via the model's reply, tool arguments, or a rendered
markdown image URL.

Goal: an opt-in feature that catches exfiltration **before it leaves**, fails closed, and gives
the model no signal about which check tripped.

### Threat model (in scope)

| # | Vector | Path out of the process |
|---|--------|-------------------------|
| T1 | System-prompt / instruction theft | assistant output text |
| T2 | Memory or RAG record theft | assistant output text, tool arguments |
| T3 | Secret leak in a URL query / path | tool arguments (web search, HTTP, MCP), markdown `![](…)` |
| T4 | Encoded leak (Base64 / hex / URL-encoded / reversed / zero-width-split) | any of the above |
| T5 | Split-across-turns smuggling | assistant output, accumulated |

### Out of scope

- Model weights, provider-side logging, network egress outside tool calls.
- Prompt-injection *prevention* (detection only, best-effort).
- Guarding a host that runs arbitrary code the model wrote.

---

## 2. Verified platform constraints

Every claim below was checked against the code in this commit, not assumed.

1. **`IGuardrailPolicy` cannot carry this feature.**
   `EvaluateAsync(IReadOnlyList<AgentMessage> output, CancellationToken)` receives output text
   only — no session id, no `AgentTurnServices`, no tool arguments
   (`MyAgents.Core/Features/IGuardrailPolicy.cs:27`). Canary state is per-session, so a policy
   instance (built once per agent definition) cannot hold it. **→ detection must be middleware
   plus a session service.**

2. **Session-scoped services are reachable from middleware.**
   `AgentAssembly.RegisterSessionService<T>(T)` (`Features/AgentAssembly.cs:84`) feeds
   `SessionOverlayServiceProvider`, whose `GetService` consults `_sessionServices` before
   delegating to root (`Agents/DefaultAgentSessionFactory.cs:151`). The overlay is what is wrapped
   in `AgentTurnServices` (`DefaultAgentSessionFactory.cs:197`). So
   `context.Services.GetService<ICanaryVault>()` works.

3. **`AgentAssembly` is per-session**, built inside `CreateSessionAsync`
   (`DefaultAgentSessionFactory.cs:41-43`), so injecting a *fresh* canary set per session is
   possible by appending to `assembly.Prompts` (`Features/PromptBuilder.cs:15`).

4. **Pre-execution tool inspection exists and can abort.**
   `IToolCallObserver.OnToolCallingAsync` is called with the raw JSON argument string *before*
   the tool runs, and throwing propagates
   (`Core/Tools/IToolCallObserver.cs:56`, fired at `Middleware/FunctionCallingMiddleware.cs:184`
   and `Runtime.Chat/ChatClientRuntime.cs:371`).

5. **Streaming can be buffered safely.** `GuardrailMiddleware.InvokeStreamingAsync` already
   accumulates the whole turn with `AgentOutputAccumulator.Fold` and yields one result
   (`MyAgents.Agents/GuardrailsFeature.cs:144-161`), with a documented rationale that leaks
   caused by a latency preference are the wrong trade. The canary middleware copies this shape.

6. **A security exception is not retried by default.**
   `DefaultTransientErrorClassifier.IsTransient` returns `true` only for
   `HttpRequestException`, `TimeoutException`, `IOException`
   (`Core/Middleware/DefaultTransientErrorClassifier.cs:10-16`), and `ResilienceMiddleware`
   retries only when `IsTransient` is true (`Core/Middleware/ResilienceMiddleware.cs:69`).
   A plain `Exception`-derived abort is therefore terminal — **but only if the app does not
   supply a broader classifier.** See risk R3.

7. **Public API is baselined.** `PublicApiApprovalTests` diffs each assembly's exported surface
   against `community/tests/MyAgents.Community.Tests/Baselines/*.approved.txt`
   (`PublicApiApprovalTests.cs:36-55`). Adding public types to Core and Agents requires
   regenerating those two baselines with `eng/PublicApiSnapshotTool` and reviewing the diff.

---

## 3. Corrections to the earlier sketch

Both were wrong; the design below is built around them.

### C1 — Tool-argument *redaction* is impossible

The sketch proposed a `RedactViolationHandler` that rewrites risky tool arguments on
medium-confidence signals. `IToolCallObserver` cannot do that: `OnToolCallingAsync` returns
`ValueTask`, `ToolCallObservation` is an immutable record, and the runtime passes the **original**
argument string to the tool (`ChatClientRuntime.cs:378`, `FunctionCallingMiddleware.cs:198`).

**Consequence:** on the tool path the only outcomes are **abort** or **allow**. Redaction is
available solely on the *output* path, where `GuardrailMiddleware` already establishes the
precedent of replacing the message wholesale.

### C2 — Redaction and "continue" are mutually exclusive on one turn

`GuardrailMiddleware.ApplyPoliciesAsync` replaces `result.Output` outright rather than appending,
because appending "would deliver both the flagged text and the substitute"
(`GuardrailsFeature.cs:172-179`). A canary handler cannot "strip the secret and keep the reply"
without inventing a new partial-redaction contract. Phase 1 does not do this; the reply is
replaced by a uniform refusal (§5.4).

---

## 4. Channels and detection

Four channels, matching the verified interception points:

| Channel | Interception point | Possible reactions |
|---|---|---|
| `TurnOutput` | middleware, after `next`, before yield | replace / refuse, quarantine |
| `ToolArguments` | `IToolCallObserver.OnToolCallingAsync` | abort, quarantine (no rewrite — C1) |
| `SubagentPrompt` | via the `DelegateTool`/`HandoffTool` argument string, i.e. the `ToolArguments` channel | as `ToolArguments` |
| `MemoryWrite` | deferred to Phase 2 (`MemoryFeature` write path) | n/a in Phase 1 |

### Scan passes (in order, cheap first)

1. Exact ordinal match of any per-session canary.
2. Normalisation retry: NFC + zero-width stripping (`U+200B–U+200D`, `U+FEFF`), then repeat (1).
3. Base64 / hex / percent-decoding of any run of ≥ 16 `[A-Za-z0-9+/=%]`, then repeat (1).
4. Reverse-string match, then repeat (1).
5. Markdown beacon: `!\[…\]\((?<url>[^)]+)\)` or `\[…\]\((?<url>[^)]+)\)` whose host is not in
   the configured allow-list, or whose query carries a high-entropy value (Shannon ≥ 4.0 bits/char
   over ≥ 20 chars).
6. Configured secret regexes (opt-in; off by default — see R4).

Findings carry `EgressSeverity`: `Confirmed` (canary match), `Suspicious` (beacons, entropy,
secret patterns), `Informational` (telemetry only).

### Explicit states (per the standard's §5)

| State | Defined behaviour |
|---|---|
| **Empty payload** | `EgressScanResult.Clean`. Never a violation — an empty turn is not "no result". |
| **Loading** | n/a — scanning is synchronous over an in-memory string; no async state exposed. |
| **Too large** | Payloads over `MaxScanBytes` (default 1 MiB) are **not** scanned; the turn is rejected with `CanaryViolationException("payload exceeds scan limit")`. The limit is named in the message. Truncating instead would let a 1 MiB prefix hide a canary at 1 MiB+1. |
| **Failure** | If a scanner throws, the result is treated as `Confirmed` and fails closed, unless `CanaryDefenseOptions.FailOpenOnScannerError = true`. Default is fail-closed. |
| **Superseded** | Quarantine is **monotonic**: once set, no later scan, handler, or retry can clear it for that session. A stale scan result is never allowed to un-quarantine. `Quarantine` is idempotent and thread-safe (`Interlocked`). |
| **Concurrent turns, same session** | Vault reads are lock-free over an immutable canary array; quarantine flips once. Two turns may both scan; the first to quarantine wins and both abort. |

---

## 5. Proposed public API

> **What "approve" means here.** These are public types in MIT-licensed NuGet packages shipped at
> `1.0.0`. Once out, a name or signature can only change in a major version, because consumers
> compile against them. Approving §5 is committing to those names; it is not a review of the
> implementation. The surface is deliberately kept to **7 public types in `MyAgents.Core.Security`
> + 4 in `MyAgents.Agents`**, with the vault, scanner, and middleware **internal** so the
> commitment stays small. Diffing the two baselines is the mechanical check.

`namespace MyAgents.Core.Security;` — new sub-namespace so the root `MyAgents.Core` namespace is
not widened by 9 types. (Decision for review: `Tools/` and `Memory/` already use sub-namespaces;
`Features/` and `Middleware/` do not. Either is defensible.)

### 5.1 `MyAgents.Core.Security`

```csharp
public enum EgressChannel { TurnOutput, ToolArguments }
public enum EgressSeverity { Informational, Suspicious, Confirmed }

public sealed record EgressFinding(
    string DetectorName,
    EgressSeverity Severity,
    string Reason);              // Reason is for logs/telemetry — never returned to the model

public sealed record EgressScanResult(IReadOnlyList<EgressFinding> Findings)
{
    public static EgressScanResult Clean { get; }
    public EgressSeverity Severity { get; }   // max over Findings
    public bool IsFlagged { get; }            // Severity == Confirmed
}

public interface IEgressScanner
{
    ValueTask<EgressScanResult> ScanAsync(
        EgressChannel channel,
        string payload,
        CancellationToken cancellationToken = default);
}

public interface ICanaryVault
{
    string SessionId { get; }
    IReadOnlyList<string> Canaries { get; }
    bool IsQuarantined { get; }
    string? QuarantineReason { get; }
    void Quarantine(string reason);           // idempotent, monotonic
}

public interface ICanaryViolationHandler
{
    ValueTask<CanaryViolationContext> HandleAsync(
        CanaryViolationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record CanaryViolationContext(
    EgressChannel Channel,
    EgressScanResult Result,
    string SessionId);

public sealed class CanaryViolationException : Exception   // NOT derived from IOException/TimeoutException
{
    public CanaryViolationException(string reason);
    public string Reason { get; }
}
```

### 5.2 `MyAgents.Agents`

```csharp
public sealed class CanaryDefenseOptions
{
    public int MaxScanBytes { get; set; } = 1_048_576;
    public bool FailOpenOnScannerError { get; set; }              // default false
    public IReadOnlyCollection<string> BeaconHostAllowList { get; set; } = [];
    public bool ScanToolArguments { get; set; } = true;
}

public sealed class CanaryDefenseFeature : IAgentFeature
{
    public CanaryDefenseFeature();                                 // defaults
    public CanaryDefenseFeature(Action<CanaryDefenseOptions> configure);
    public CanaryDefenseFeature(IEgressScanner scanner, Action<CanaryDefenseOptions>? configure = null);
    public void Contribute(AgentAssembly assembly);
    // 1. assembly.RegisterSessionService<ICanaryVault>(vault)
    // 2. assembly.Prompts.AddSystem(canaryDirective)
    // 3. assembly.Pipeline.Use(new CanaryEgressMiddleware(...))
    // 4. assembly.ToolObservers.Add(new CanaryToolEgressObserver(...))
}

public sealed class QuarantineSessionViolationHandler : ICanaryViolationHandler   // default
public sealed class AuditOnlyViolationHandler : ICanaryViolationHandler           // rollout/shadow
```

`SessionCanaryVault`, `CanaryEgressMiddleware`, and `CanaryToolEgressObserver` stay **internal**
to keep the approved public surface small; only the two handlers, the options, and the feature are
public.

### 5.3 Canary generation

`ICanaryGenerator` (Core, internal use) with `RandomCanaryGenerator` as the default producing
`"CANARY-" + 128 random bits, base32`. Deterministic replacement for tests lives in
`MyAgents.Testing` as `StaticCanaryGenerator`, so adversarial tests are reproducible.

### 5.4 Reaction policy

| Severity | `TurnOutput` | `ToolArguments` |
|---|---|---|
| `Confirmed` | abort turn with uniform refusal + quarantine session | throw `CanaryViolationException` *before* the tool runs + quarantine |
| `Suspicious` | configurable: `Audit` (default in Phase 1) or `Abort` | configurable: `Audit` (default) or `Block` |
| `Informational` | telemetry only | telemetry only |

**Uniform refusal.** The caller sees one fixed string — `"I'm sorry, but I cannot provide a
response to that request."` — identical to `GuardrailMiddleware.GenericFallbackMessage`, and
**`Reason` is never surfaced to the model or caller**. Telling the model "blocked: canary X" hands
a probing attacker an oracle.

**No history poisoning.** A quarantined turn's output is not appended to durable memory or graph
history. Phase 1 enforces this by aborting before the write; snapshot rollback hooks into
`AgentGraph` in Phase 2.

---

## 6. Implementation plan

| Step | Deliverable | Files |
|---|---|---|
| 1 | Contracts (`EgressChannel`, `EgressSeverity`, `EgressFinding`, `EgressScanResult`, `IEgressScanner`, `ICanaryVault`, `ICanaryViolationHandler`, `CanaryViolationContext`, `CanaryViolationException`) | `MyAgents.Core/Security/*.cs` — one type per file |
| 2 | `SessionCanaryVault` (internal, thread-safe, monotonic quarantine) | `MyAgents.Core/Security/` |
| 3 | `DefaultEgressScanner` + normalisation passes (internal) | `MyAgents.Core/Security/` |
| 4 | `MarkdownBeaconDetector`, `HighEntropyDetector` (internal) | `MyAgents.Core/Security/Detectors/` |
| 5 | `CanaryEgressMiddleware` — buffered streaming, mirrors `GuardrailMiddleware` | `MyAgents.Agents/` |
| 6 | `CanaryToolEgressObserver` — throw-before-invoke | `MyAgents.Agents/` |
| 7 | `CanaryDefenseFeature`, `CanaryDefenseOptions`, two handlers | `MyAgents.Agents/` |
| 8 | `StaticCanaryGenerator` | `MyAgents.Testing/` |
| 9 | Regenerate `MyAgents.Core` + `MyAgents.Agents` baselines | `community/tests/.../Baselines/` |
| 10 | Docs page | `docs-src/docs/governance/canary-egress-defense.md` + `toc.yml` |

Warnings are errors and code style is enforced in build (`Directory.Build.props`), so XML docs are
required on every public member.

---

## 7. Test strategy

New fixture `community/tests/MyAgents.Community.Tests/CanaryEgressDefenseTests.cs`, NUnit,
`MyAgents.Testing` doubles only (no live provider), driven through a `TestChatClient` copy of the
pattern at `GuardrailsAndRoutingTests.cs:17`.

Per the standard's §4, each adversarial test must be **seen red first**:

1. **Canary in output** → assert the turn result content equals the uniform refusal **and** that a
   non-canary turn through the identical pipeline returns the model's real text (proves the
   middleware is not simply blanking every turn).
2. **Base64-wrapped canary in output** → same assertions. Then temporarily disable pass 3 and
   confirm the test goes red for the right reason (a miss, not a compile error).
3. **Zero-width-split canary** → same pattern.
4. **Canary in tool arguments** → assert the tool's `InvokeAsync` was **never** reached
   (a throwing spy tool), not merely that an exception was thrown.
5. **Quarantine is monotonic** → quarantine, then run a clean turn, assert it still aborts.
6. **Scanner throws** → assert fail-closed (abort) with defaults, and fail-open with
   `FailOpenOnScannerError = true`.
7. **Payload over `MaxScanBytes`** → assert rejection names the limit.
8. **Empty output** → assert `Clean`, no violation.
9. **Streaming parity** → same canary turn via `InvokeStreamingAsync` produces one refusal chunk,
   not the leaked prefix.
10. **No oracle** → assert `Reason`/`DetectorName` appear in neither the result content nor the
    thrown exception message.

A test asserting only "text is not shown" is insufficient per §4 — every case above pairs it with
a positive assertion.

---

## 8. Risks and open questions

**R1 — False positives on legitimately quoted prompts.** A model asked "repeat your instructions"
will trip pass 1. Mitigation: the canary is a high-entropy token, not prose, so a faithful quote is
exactly the leak we want to catch. Accepted.

**R2 — Detection is not prevention.** An attacker who knows the scheme can exfiltrate one bit per
turn via a side channel that never touches a scanned channel. The feature raises cost; it does not
close the hole. Must be stated plainly in the docs page.

**R3 — A custom `ITransientErrorClassifier` could make a security abort retryable. — RESOLVED**
`DefaultTransientErrorClassifier` treats only `HttpRequestException`/`TimeoutException`/
`IOException` as transient (`Core/Middleware/DefaultTransientErrorClassifier.cs:10-16`), so a plain
`Exception`-derived abort was already terminal **by default**. The hole was an application injecting
`IsTransient => true`: `ResilienceMiddleware` would then replay the aborted turn.

**Fixed now.** `MyAgents.Core.Middleware.INonRetryableException` is a marker interface; any
exception implementing it is rethrown without a retry attempt, regardless of what the classifier
reports. `ResilienceMiddleware` checks it in all three catch filters (blocking ×2, streaming ×1).
`CanaryViolationException` will implement it in Phase 1.
Verified by three tests in `ResilienceAndCoreUtilitiesTests.cs`, all of which were seen red first:

- `DoesNotRetry_NonRetryableException_EvenWhenClassifierSaysTransient` → attempts `1` (was `3` with the guard removed)
- `StillRetries_TransientException_UnderTheSameClassifier` → attempts `3` (control: proves the guard is keyed on the marker, not on disabling retries)
- `Streaming_DoesNotRetry_NonRetryableException` → attempts `1` (was `3`)

Public API cost: one interface, one added baseline line, no behaviour change for any existing caller.

**R4 — Secret-pattern regexes are opt-in and off by default.** A misfiring API-key regex refuses
legitimate turns in production. Teams should enable them only after shadow mode (R6).

**R5 — Buffered streaming costs latency.** Same trade `GuardrailMiddleware` already made; an agent
with canary defense gets no token-by-token streaming. Documented, not configurable in Phase 1.

**R6 — Rollout.** `AuditOnlyViolationHandler` exists so teams can measure trigger rates on real
traffic before enforcing. This is the recommended first deployment.

**R7 — Canary in the system prompt is only as good as the provider's prompt handling.** Some
providers truncate or summarise long system prompts; a truncated canary is a dead canary. Phase 1
appends the directive at the end of the assembled prompt to minimise truncation risk; not verified
per provider.

---

## 9. Decisions

| # | Decision | State |
|---|---|---|
| 1 | Public API in §5, incl. the `MyAgents.Core.Security` sub-namespace | **OPEN** — the only remaining gate |
| 2 | `Suspicious → Audit` as the Phase 1 default | **SETTLED** — Audit is the default |
| 3 | R3 fix now vs Phase 2 | **SETTLED — fixed now** (§8 R3) |
| 4 | `design/` as the RFC home | **SETTLED — tracked in git, outside the published site** |

### 9.4 `design/` is tracked

The RFC lives in `design/`, which is versioned and therefore reviewable in the PR. It sits outside
`docs-src/docfx.json`'s content globs, so it is deliberately **not** published to the site.

The §8 risk register (R1–R7) is the record of why the defaults are what they are, so keeping it
under version control is what stops those decisions losing their written justification later.

Phase 1 is implemented; see §10 for the shipped state and the deviations from §5.

---

## Not checked

- No provider behaviour was exercised: canary survival through any real model's system-prompt
  handling (R7) is unverified.
- The entropy threshold (4.0 bits/char over ≥ 20 chars) and `MaxScanBytes` are **untuned guesses**,
  not measured against real traffic.
- Base64/hex decoding is described for runs of `[A-Za-z0-9+/=%]`; whether that catches
  URL-safe Base64 and hex with separators was reasoned about, not tested.
- `MemoryWrite` as a channel was not traced end-to-end — `MemoryFeature`'s write path was not read
  for this RFC, so Phase 2's hook point is assumed, not verified.
- `MyAgents.Testing` API baselines were not inspected; adding `StaticCanaryGenerator` there will
  likely require a third baseline regeneration, which §6 step 9 currently omits.

---

## 10. Implementation notes — what shipped vs. what §5 said

Phase 1 is implemented on this branch. Deviations from §5, each deliberate:

### 10.1 `CanaryDisposition` added (10 public types, not 9)

§5 had `ICanaryViolationHandler.HandleAsync` return `CanaryViolationContext`, which carries no
decision and so cannot express "abort" or "allow". Replaced with
`ValueTask<CanaryDisposition> HandleAsync(...)` and a new
`MyAgents.Core.Security.CanaryDisposition { Allow, Abort }` enum.

### 10.2 The handler — not options — is the reaction policy

§5.4 had `Suspicious` abort controlled by options flags. Shipping both an options flag *and* a
handler gave two places to express the same decision, so the options were reduced to **detection
only** (`MaxScanBytes`, `FailOpenOnScannerError`, `BeaconHostAllowList`, `ScanToolArguments`).
Defaults now follow from the default handler:

- `QuarantineSessionViolationHandler` (default): `Confirmed` → quarantine + Abort; `Suspicious` → Allow.
- `AuditOnlyViolationHandler`: Allow everything. **Shadow mode only.**

**Correction to §5.4:** "`Confirmed` always fails closed" is true *under the default handler*, not
unconditionally — `AuditOnlyViolationHandler` lets a confirmed canary through by design. That is
asserted explicitly by `An_allowing_handler_still_lets_a_confirmed_canary_through` so the hole cannot
drift silently, and the handler's XML doc says do not ship it to production.

### 10.3 `AgentAssembly.SessionId` added

`IAssembly` had no session id, but `ICanaryVault.SessionId` needs one and a canary must be unique per
session. Added `public string? SessionId { get; internal set; }` to `AgentAssembly`, assigned in
`DefaultAgentSessionFactory.CreateSessionAsync` before features contribute. One additive public
property; benefits any future session-aware feature.

### 10.4 No `StaticCanaryGenerator` in `MyAgents.Testing`

§6 step 8 is dropped. Tests obtain the canary the way an attacker would — by reading the system
prompt the framework actually sent. That needs no test double, exercises the real injection path,
and avoids a third baseline regeneration.

### 10.5 Beacon heuristic is narrower than §4

§4 flagged any markdown link or image whose host is not allow-listed. Shipping that would fire on
every citation. Narrowed: **images** (auto-fetched, silent one-way channel) are `Suspicious` on an
unknown host; **plain links** only when the query string is high-entropy. Recorded here because it
is a real reduction in coverage, not just an implementation detail.

### 10.6 Tool-argument redaction confirmed impossible (C1 held)

`CanaryToolEgressObserver` only aborts. No rewrite path exists — see C1.

### 10.7 Verification performed

`dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test`: **235/235 pass**, up from 218.

New tests — 14 in `CanaryEgressDefenseTests`, 3 in `ResilienceAndCoreUtilitiesTests`. Every
detection test was **seen red first** with the behaviour broken:

| Break | Tests that went red | Tests that stayed green |
|---|---|---|
| Removed the Base64/hex decoding pass | `Base64_wrapped_canary_is_detected` | the other 13 |
| Disabled the tool-channel abort | `Canary_in_tool_arguments_aborts_before_the_tool_runs` | the other 13 |
| Removed the `INonRetryableException` guard | the 2 non-retryable tests | the control test |

Baselines regenerated for `MyAgents.Core` (+59 lines) and `MyAgents.Agents` (+21 lines). Both diffs
are purely additive.

### 10.8 Still outstanding

- `docs-src/docs/governance/canary-egress-defense.md` + `toc.yml` (§6 step 10) not written, because
  it regenerates the whole committed `/docs` site (~326 files) and should be its own commit.
- `MemoryWrite` channel (R2/Phase 2) not implemented; `MemoryFeature` write path still unread.
- Thresholds still untuned: `MaxScanBytes` = 1 MiB, entropy ≥ 4.0 bits/char over ≥ 20 chars.

---

## 11. Threat T5 — cross-turn smuggling (implemented)

Phase 1 scanned each turn in isolation, so the cheapest evasion worked: emit one fragment per turn
and the token never appears whole in any single payload.

### 11.1 Two changes, because the obvious one is not enough

**`CanaryTurnWindow`** — a bounded per-session ring of recent turn output, scanned together with the
current turn. Bounds are **both** enforced, oldest evicted first:

| Bound | Default | Why |
|---|---|---|
| `DefaultTurns` | 8 | A split spanning more turns than this is beyond what a window can catch; unbounded retention would grow scan cost without limit. |
| `DefaultBytes` | 8 KiB UTF-8 | Clamped to half of `MaxScanBytes` at wiring time, so the combined payload (window + turn) cannot trip the oversize guard and fail every turn closed. |

Eviction is a **correctness** property, not an optimisation: without it the window keeps matching
fragments the session moved past long ago, and the scanned payload grows until every turn is
rejected as oversized.

**`CanaryMatcher.MatchFragments`** — plain concatenation is insufficient. Each turn carries its own
prose, so the fragments are never adjacent in the combined payload; the first implementation failed
exactly this way (the completing fragment was matched against nothing and leaked). This pass strips
every character that cannot belong to a canary, then matches the token exactly — which reassembles
fragments regardless of how much text sits between them, or how many turns the split spans. The
alphabet is derived from the configured canaries rather than hard-coded, so a token-format change
cannot silently disable the pass.

### 11.2 Defect found and fixed during implementation

Truncating an oversized turn to `maxBytes` **characters** is not a byte bound: a character can cost
up to four bytes, so a 40-character string can occupy 80 bytes. Caught by a UTF-8 accounting test
before the feature was committed. Truncation now walks back by byte cost and keeps surrogate pairs
whole.

### 11.3 Verification

Full suite **247/247** (236 before). Every new behavioural test was seen red first, or proven
breakable by breaking the code:

| Break | Red | Green |
|---|---|---|
| Window unplugged from the feature | the 3 split tests, including the "near" direction of the bound test | the other 15 |
| Eviction removed from `Append` | the 2 bounds unit tests and the "far" direction of the bound test | the matching path |

The "near/far" test asserts **both** directions in one test deliberately: asserting only the far case
would pass just as happily with no window at all.

`InternalsVisibleTo` for the test project is a visibility change only — no public API change, no
baseline regeneration.

### 11.4 What T5 coverage still does not reach

- A split spanning **more than 8 turns**, or whose fragments exceed the byte budget, is not caught.
  Both are configured constants, not tunables, because exposing them would be public API.
- Fragment matching is order-preserving over filtered text, so an attacker who interleaves
  *canary-alphabet characters* between fragments defeats it. Cost is raised, not eliminated.
