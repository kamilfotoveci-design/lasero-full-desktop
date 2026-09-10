# Web + WPF account/entitlement architecture audit

**Status: Phase 1 and Phase 2 implemented and reviewed — 2026-09-10.** See "Phase 1 implementation
status" and "Phase 2 implementation status" near the end of this file for exactly what shipped, what
was deliberately left out, and what's still queued. The audit body below is unchanged from the
original output-only pass.

Originally written 2026-09-10 (session 10 continuation).
Covers the two codebases below with the goal of eventually sharing one `UserId`, one account/
entitlement model, one KAMIL chat history, one work history, one materials/recipes store —
**without disrupting the web app's existing live PRO users.**

- Web (production, live PRO users): `C:/Users/Ruzovka/Videos/lasero-app`
- Desktop (WPF, private/in-dev): `E:/lasero-desktop`, project `Lasero.App`

No production billing, live web UX, or desktop-download exposure was touched while producing this
report. Everything below is read-only findings plus a proposal.

---

## 0. Headline finding

**The foundation is already unified more than expected.** Both apps point at the same Firebase
project (`lasero-73bc3`), both already treat the **Firebase UID as the canonical user identifier**
(not email), and the desktop app already calls the web app's real production entitlement backend
(`check-premium`, `redeem-license`) and a real, working, revision-based cloud sync endpoint
(`sync-materials`) that **three clients — web, a Capacitor mobile app, and desktop — already share**.
This is not a greenfield unification problem; it's mostly a documentation-and-small-fixes problem,
plus two genuinely unbuilt pieces (chat sync, project/work-history sync).

One correction to the brief's assumption: the web app is **not** a Next.js app. It's a static-file
site (`netlify.toml` → `publish = "."`) with the entire client UI in a single ~1.5 MB `index.html`,
and all server logic in Netlify Functions backed by **Netlify Blobs** (a key-value JSON store), not
Firestore or any SQL/NoSQL database.

---

## 1. Current web identity model

- Firebase Web SDK, project `lasero-73bc3`, config inline at `index.html:23390-23403`. Same Firebase
  Web API key (`AIzaSyCpZ7uvUCoA1xjqo60Z8qcGDeDqJ1zihaI`) is hardcoded in the web client, every
  relevant Netlify function's fallback, *and* the desktop app's `LaseroAuthClient` — confirmed same
  Firebase project across web, mobile, and desktop.
- Canonical identifier everywhere on the backend: **Firebase UID**. Every function that needs
  identity either verifies a Bearer ID token via `identitytoolkit.googleapis.com/v1/accounts:lookup`
  (`redeem-license.js`, `gemini.js`, shared helper `_shared/firebase-auth.js`) or accepts `uid`
  directly (`check-premium.js`, `verify-play-purchase.js`, `verify-apple-purchase.js`,
  `register-user.js`). All per-user Blobs stores are keyed by UID.
- A real mobile app already exists and shares this backend: `verify-play-purchase.js` /
  `verify-apple-purchase.js` confirm a Capacitor-based Android/iOS app (`clientId: 'mobile'` in
  `assets/material-sync.js:103`) is a third live consumer of the same entitlement + materials-sync
  system. Any unified model should treat this as a third surface, not just web+desktop.

## 2. Current WPF identity model

- `Lasero.Core.LaseroApi.LaseroAuthClient` — Firebase REST auth (`signInWithPassword`,
  `securetoken` refresh), same Firebase project/API key as web.
- `SessionStore` — DPAPI-protected local file (`%LocalAppData%\Lasero\session.dat`), stores
  `{RefreshToken, LocalId, Email}`. **`LocalId` (the Firebase UID) is the field actually used as the
  identity key**; `Email` is carried only for UI display (pre-filling the login form), never used as
  a lookup key anywhere in the desktop codebase.
- `AccountViewModel.UserId` = `_session.LocalId` — already the correct canonical ID, already
  consistent with the web/mobile model. No identity-model change is needed on desktop.
- `LaseroAccountClient` calls the **same production endpoints** web/mobile use:
  `check-premium?uid=` and `redeem-license` (Bearer ID token). Desktop is not a placeholder client —
  it already talks to the real entitlement backend.

## 3. PRO/subscription model

Four independent, OR-combined entitlement sources, all converging on one Netlify Blobs record per
UID in the `lasero-premium` store:

1. **Stripe subscription** (primary/source-of-truth path) — `create-checkout.js` (multi-currency
   Checkout session), `stripe-webhook.js` (`checkout.session.completed` activates,
   `customer.subscription.deleted` revokes, `customer.subscription.updated` handles
   `active`/`trialing` vs `past_due`/`unpaid`), `create-portal-session.js` (self-service billing
   portal).
2. **Manual license keys** — `redeem-license.js` + `create-license.js`. Two code sources: a
   **hardcoded plaintext `STATIC_KEYS` map of 40 codes** (20 monthly/20 yearly) and an
   admin-generated `lasero-licenses` Blobs store.
3. **Promo codes** — `redeem-promo.js` / `create-promo.js` / `validate-discount.js`.
4. **Mobile IAP** — `verify-play-purchase.js` (Google Play Developer API, service-account JWT) and
   `verify-apple-purchase.js` (App Store Server API, ES256 JWT). Client-initiated verification calls,
   not server-to-server webhooks/RTDN — cancellations/renewals on mobile only reflect next time the
   client re-verifies or calls `check-premium`.

**Source of truth**: `lasero-premium` Blobs store, one JSON blob per UID:
`{isPremium, promoExpiry, licenseExpiry, playSubscriptionExpiry, appleSubscriptionExpiry, trialStart,
+ Stripe/license/promo/IAP metadata}`. There is no single canonical `isPremium` boolean — it's
recomputed at read time in at least two places (`check-premium.js` and `gemini.js`), and **these two
have already drifted**: `gemini.js:77` computes `paidPremium = stripeActive || licenseActive ||
playActive` — **omitting `appleActive`**. An Apple-subscribed user whose trial has expired could be
denied Kamil chat access even though `check-premium` correctly reports them as premium. Pre-existing
bug, unrelated to desktop, worth a one-line fix regardless of the unification work.

## 4. Entitlement/license model (desktop consumption)

Desktop already consumes this correctly:
- `AccountViewModel.RefreshEntitlementAsync()` calls `CheckPremiumAsync(uid)` → sets
  `IsPremium`/`TrialDaysLeft` from the same live blob every other client reads.
- `RedeemLicense()` calls the same `redeem-license` endpoint with a fresh ID token.
- No separate/parallel desktop entitlement system exists. This is good — it means "one entitlement
  model" is **already true today**, not a future goal.

What doesn't exist yet, anywhere in the backend: **per-surface access flags** (web vs desktop access
as separate booleans) and **device activation tracking** (`MaxDevices`/`ActivatedDevices` from the
brief's proposed shape). Today, one PRO account can run on unlimited desktop installs with zero
tracking — this is not a regression desktop introduces, it's simply a dimension the current model
never needed until now.

## 5. Existing reusable APIs

Reusable as-is, no changes needed to unify identity:
- Firebase Auth REST (`identitytoolkit.googleapis.com`) — already identical on both clients.
- `check-premium.js` — reusable as-is. **Caveat: no auth at all**, plain `?uid=` GET. Low-severity
  info leak (anyone who knows/guesses a UID can read their premium/trial status) — acceptable today,
  worth revisiting if the desktop download becomes public and this endpoint's UID becomes easier to
  discover client-side.
- `redeem-license.js` — reusable as-is.
- `sync-materials.js` + its `_shared/firebase-auth.js` `requireFirebaseUser()` helper — reusable
  as-is, **and the pattern itself (Bearer auth + revision-based optimistic concurrency + 409-with-
  server-snapshot conflict handling) is the correct template to copy** for any future chat-history or
  project-history sync endpoint. It's already proven across three real clients.
- `gemini.js` — reusable as-is for desktop KAMIL (already wired via `LaseroChatClient`). Fix the
  Apple-omission bug (§3) independent of any unification work.

Existing WPF pieces reusable as-is: `LaseroAuthClient`, `LaseroAccountClient`, `SessionStore`,
`AccountViewModel`, `MaterialSyncClient`. This stack already implements "share one UserId" — no
rework required.

## 6. Data-ownership map

**CLOUD-AUTHORITATIVE** (server/Blobs owns the truth; a client must never invent or override this):
- Identity/auth (Firebase Auth)
- Entitlement/premium status (`lasero-premium`)
- License keys (`lasero-licenses`, static-key list)
- Stripe subscription state
- Promo/discount codes (`lasero-promos`, `lasero-discounts`)
- User registry (`lasero-users` — email/display name/CRM sync, not identity)
- Community showcase posts/images, affiliate/referral accounts, activity/gamification score
  (product features, not account-architecture-relevant, noted for completeness only)

**LOCAL-FIRST** (device owns the working copy; no cloud copy exists today, and for some of these
none should ever exist):
- KAMIL chat history — **both** web (`localStorage`, `lp_chat_sessions_<uid>`) and desktop
  (`ChatStore`, JSON keyed by `SHA256(uid)` under `%LocalAppData%\Lasero\chat\`) are local-only.
  No cloud copy exists anywhere in the system today.
- Desktop `.lasero` project files, recent-projects list, project recovery — real files on disk,
  correctly local-first, no cloud copy today.
- Desktop app/machine settings (`AppSettingsStore` — device port, safety flags, machine profile,
  panel sizing). **Should stay local-first permanently** — this is genuinely device-specific data,
  not a sync candidate, and the store's own doc comment already says so correctly.
- Web saved SVG designs (`lp_saved_designs`, local-only, capped at 20, **never synced** — confirmed
  no `sync-designs`/`sync-projects` function exists).
- Web UI prefs (`lp_theme`, `lp_lang`, `lp_profile`) — local-only, not account data.

**SYNCABLE** (already has a working cloud round-trip, or is the right shape to get one):
- Personal material/recipe presets — **already fully implemented**, three clients, working today.
- Web's saved parameter presets (`lp_saved_params`) — already routed through the same
  `sync-materials` channel when signed in (confirmed: `getSavedParams()`/`saveParam()` delegate to
  `window.LaseroMaterialSync`).
- *(proposed, not yet built)* KAMIL chat history — natural candidate for a new `sync-chat-history`
  function mirroring `sync-materials.js` exactly.
- *(proposed, not yet built, largest lift)* work/project history — the least-precedented item; web
  has no real "project" concept to mirror (only ephemeral saved-params/saved-designs), and desktop's
  `.lasero` files are real documents, not small JSON records like a material preset. Needs its own
  design pass, not a copy of the materials-sync pattern.

## 7. Anything keyed by email instead of a stable UserId

Narrow, contained, low-urgency — not a systemic problem:
- `admin-grant-premium.js` (grant-by-email path) and `stripe-webhook.js`'s customer→UID resolution
  both **linear-scan the entire `lasero-users` Blobs store** looking for a matching `email` field —
  there is no secondary email→UID index. O(n) per call; fine at current scale, will not be fine at
  10k+ users. Admin-tool-only, not user-facing, low priority.
- `register-user.js` writes `email` as a *field* on the UID-keyed `lasero-users` blob — correct
  direction (UID is the key, email is data), not a smell.
- Desktop's `SessionStore` carries `Email` alongside `LocalId`/`RefreshToken` purely for pre-filling
  the login form on next launch — never used as a lookup key. Not a smell.
- No systemic "identity keyed by email" problem exists on either side. The only real instances are
  the two admin-tool linear scans above.

## 8. Duplicate state between web and desktop

- **Entitlement/premium status** — NOT duplicated. Both read the same live `lasero-premium` blob via
  `check-premium`; each client's local cache (desktop's in-memory `IsPremium`/`TrialDaysLeft`, web's
  `lp_promo_premium_<uid>`/`lp_license_expiry_<uid>` localStorage cache) is a short-lived display
  cache, not a second source of truth.
- **Material presets** — NOT duplicated in a harmful way. Genuinely synced, single server-side source
  of truth, each client holds a cache with proper revision tracking.
- **KAMIL chat** — **duplicated and divergent**. Web and desktop each maintain a completely separate
  local-only chat history per account, with zero reconciliation. A user who chats with Kamil on web
  and then opens desktop sees no continuity — two unrelated histories for "the same" assistant. This
  is the clearest concrete gap behind the "one KAMIL chat history" goal in the brief.
- **Saved settings vs. material presets vs. machine profiles** — three overlapping-but-not-identical
  concepts: web's saved-params (name/mode/speed/power/passes/dpi), desktop's material presets
  (name/mode/speed/power/passes/fillLineIntervalMm — already the same entity as web's, via
  `sync-materials`), and desktop's **separate** `MachineProfilePreferences.Last*` fields (last-used
  power/speed/passes *per physical machine*, in `AppSettingsStore`, correctly local-only). The first
  two are already effectively unified; the third is intentionally a different concept (per-machine
  last-used state, not a named recipe) and should stay separate — flagging only so a future session
  doesn't try to merge it by mistake.
- **Recent projects / saved designs** — not a harmful duplication today. Desktop has real persisted
  project files with a real recent-list and recovery; web only has ephemeral in-browser SVG saves.
  These reflect genuinely different product capabilities (desktop authors real documents; web is a
  lighter in-browser tool) — likely correct to leave conceptually separate rather than force a merge,
  until "one work history" is scoped as its own deliberate project.

## 9. Proposed unified UserProfile model

No migration is required for the identity half of this — `UserId` is already the Firebase UID
everywhere. This model is additive: a read/aggregation layer over data that already exists, not a
rewrite of where entitlement truth lives.

```
UserProfile {
  UserId: string              // Firebase UID — already canonical on web, mobile, and desktop
  Email: string                // display/contact only, never a lookup key
  DisplayName: string?
  CreatedAt: DateTimeOffset
  LastSeenAt: DateTimeOffset
  Entitlement: Entitlement
}
```

## 10. Proposed entitlement model

```
Entitlement {
  UserId: string
  Plan: enum { Free, Trial, Pro }
  Status: enum { Active, Trialing, PastDue, Canceled, Expired }
  Source: enum { Stripe, License, Promo, GooglePlay, AppleAppStore, Grandfathered }
  WebAccess: bool              // = Plan != Free today; every current PRO user already has this
  DesktopAccess: bool          // NEW concept — does not exist in the backend today
  LicenseId: string?           // canonical id of whichever Source currently grants access
                                // (Stripe subscriptionId / license key / IAP transaction id)
  ExpiresAt: DateTimeOffset?
  MaxDevices: int?             // NEW — null = unlimited (today's de facto, unenforced behavior)
  ActivatedDevices: DeviceActivation[]   // NEW, additive
}

DeviceActivation { DeviceId: string, Platform: enum { Web, Desktop, AndroidApp, iOSApp }, FirstSeenAt, LastSeenAt }
```

`Plan`/`Status`/`Source`/`LicenseId`/`ExpiresAt` are all **derivable today** from the existing
`lasero-premium` blob's `isPremium`/`promoExpiry`/`licenseExpiry`/`playSubscriptionExpiry`/
`appleSubscriptionExpiry`/`trialStart` fields — this can ship as a computed view or a new read-only
endpoint, without touching the write paths (`stripe-webhook.js`, `redeem-license.js`, IAP verifiers)
that current PRO users' billing actually depends on. `WebAccess`/`DesktopAccess`/`MaxDevices`/
`ActivatedDevices` are the only genuinely new pieces of state.

## 11. Safe grandfathering strategy for existing PRO users

Given the entitlement backend already OR-combines four grant sources into one `isPremium` per UID,
the simplest safe rule is:

> **Any UID with a currently-active grant (via any existing source) automatically gets
> `DesktopAccess = true`, with no new gate, no new purchase, no device cap enforced.**

Concretely: `DesktopAccess := WebAccess` for every existing user, forever, unless a future business
decision explicitly splits them (e.g. desktop becomes a paid upsell). This requires **zero backend
schema change** and **zero live web UX change** — desktop simply keeps calling the same
`check-premium`/`redeem-license` endpoints it already calls today, which already return the correct
answer for every existing PRO user. The only new work is *additive*: start passively logging device
activations (a new field/store, e.g. `lasero-device-activations`) so real usage data exists before
anyone decides whether/how to enforce `MaxDevices` later. No current user's access should ever change
as a result of that logging.

### 11a. Formally approved grandfathering policy (confirmed 2026-09-10, Phase 2)

This is not a proposal anymore — it is the approved policy this project operates under, until a
future business decision explicitly changes it:

> **Any existing active PRO entitlement (via any of the four current sources — Stripe, license key,
> Google Play, or Apple App Store) automatically grants desktop access.**

Concretely, and explicitly:

- **No separate desktop purchase.** A PRO web/mobile subscription is a PRO subscription, full stop —
  desktop is an additional surface for the same entitlement, not a new product to buy.
- **No new desktop-specific entitlement gate.** Desktop continues calling the exact same
  `check-premium`/`redeem-license` endpoints described in §4/§5 above, unmodified. There is no
  `DesktopAccess` field, flag, or check anywhere in the code today, and none is being added by this
  policy — the existing `isPremium`/`trialDaysLeft` response IS the desktop answer too.
- **No device cap.** `MaxDevices`/`ActivatedDevices` enforcement (§10's proposed model) is explicitly
  **not** part of this policy and is not being built in this phase (see Phase 2 status below — only
  passive, unenforced logging exists).
- **No entitlement schema migration.** The `lasero-premium` Netlify Blobs record shape is untouched.
- **No retroactive restriction.** This policy can only ever grant continuity for existing users, never
  take access away from someone who had it before this policy existed.
- Desktop's `LaseroAccountClient`/`AccountViewModel` stack (§2/§4) already implements this correctly
  today — this section documents the policy that code already embodies, it does not change any code.

If this policy is ever revisited (e.g. desktop becomes a paid upsell, or a device cap is introduced),
that is a new, separate, explicitly-approved decision — not an extension of this one.

## 12. Minimal required changes — web side

1. **Fix `gemini.js`'s Apple-entitlement omission** (§3) — isolated, low-risk correctness fix,
   unrelated to unification but found during this audit. Does not change behavior for any
   Stripe/license/Play user.
2. **No live web UX change needed for phase 1.** Desktop already consumes the real backend
   correctly; grandfathering (§11) requires no new web-side logic, only a policy decision.
3. *(Low priority, optional)* Add an email→UID secondary index to remove the O(n) admin-tool scans
   (§7) — not urgent at current scale.
4. *(Only if/when device-activation logging is approved)* Add a new additive endpoint or field to
   record `{uid, deviceId, platform, timestamp}` on sign-in — must not block or alter existing
   sign-in/entitlement responses.

## 13. Minimal required changes — WPF side

1. **Scope `MaterialPresetStore`'s local cache file by UID**, the same way `ChatStore` already does
   (`SHA256(uid)` filename instead of one shared `materials.json`). Small, safe, no server-side
   change, no schema change to the sync protocol — purely fixes a local-cache-only inconsistency.
   Self-healing today only because the next successful sync overwrites stale data; worth fixing
   regardless of the unification project since it's a one-line-pattern-reuse fix.
2. **Scope `RecentProjectsStore`'s local cache file by UID**, same pattern. Currently a single global
   `recent-projects.json` regardless of which account is signed in — the one real
   cross-account-bleed risk found in this audit (see §14). No cloud sync exists for this data at all,
   so unlike materials it will **not** self-heal; a second account signing into the same Windows
   profile would see the first account's recent-project names/thumbnails until they're manually
   overwritten.
3. **No entitlement-model change needed.** `LaseroAccountClient`/`AccountViewModel` already implement
   the "one UserId, one entitlement" goal correctly today.
4. *(Only if/when approved)* Add a device-registration call from
   `AccountViewModel.TryResumeSessionAsync`/`SignIn` — purely additive telemetry, must not gate or
   delay sign-in, must not change `IsSignedIn`/`IsPremium` semantics.

## 14. Risks

- **`check-premium.js` has no auth** — low-severity UID-based info leak, pre-existing, not introduced
  by desktop work; revisit if/when the desktop download becomes public and UID discoverability
  increases.
- **`gemini.js` Apple-entitlement omission** (§3) — real, pre-existing correctness bug independent of
  this audit's scope; worth fixing on its own merits.
- **No `MaxDevices` enforcement exists anywhere today** — not a new risk from desktop, but the
  proposed model anticipates needing it before a wide release; must be additive-only when built (see
  §11) so no current user is retroactively restricted.
- **`RecentProjectsStore` not UID-scoped** (§13.2) — the one concrete cross-account privacy/confusion
  risk this audit found on the desktop side; recommend fixing ahead of, and independent from, the
  rest of the unification work.
- **KAMIL chat and desktop project files have zero cloud presence today** — "one KAMIL chat history"
  and "one work history" are the two goals from the original brief with **no existing backend to
  build on**. Treat both as net-new, dedicated-design-session work, not a refactor of something that
  already exists. Do not estimate them as "just wire up sync-materials again" — the materials pattern
  transfers cleanly to chat (small JSON records, same shape), but not to project files (larger,
  binary-adjacent, need a real storage-size/versioning strategy).
- **Netlify Blobs is a key-value store, not a queryable database** — the existing O(n) admin scans
  (§7) are a preview of what will need re-architecting if the user base or admin tooling grows
  significantly; not urgent, but any new per-user feature (like device-activation tracking) should be
  designed with this ceiling in mind rather than adding another linear-scan pattern.
- **40 static license keys are plaintext in the web repo's source** (`redeem-license.js`) — pre-existing,
  unrelated to unification, but worth flagging since it means anyone with read access to
  `lasero-app` already has all 40 codes; not something this project should touch, just noted for
  awareness.

## 15. Recommended implementation order

1. **(WPF, do first, safe now)** Fix `MaterialPresetStore` + `RecentProjectsStore` local-cache
   scoping to hash-by-UID, matching `ChatStore`'s existing pattern. Zero server changes, zero risk to
   live web users, immediately removes the one real cross-account-bleed risk found.
2. **(Web, safe now, independent)** Fix `gemini.js`'s Apple-entitlement omission. Isolated,
   no schema change, no behavior change for existing Stripe/license/Play users.
3. **(Decision, no code)** Confirm the grandfathering policy from §11 in writing: existing
   PRO = automatic `DesktopAccess`, no new gate, no device cap enforced yet.
4. **(Additive, low risk)** Add passive device-activation logging server-side (new field/store only,
   never blocks sign-in), then a device-registration call from desktop's `AccountViewModel` sign-in
   path. Telemetry only — no enforcement yet.
5. **(New scoped work, moderate size)** Build `sync-chat-history` following `sync-materials.js`'s
   proven pattern (same auth helper, same revision/conflict protocol) — only after 1-4 are stable.
   Wire into `ChatStore`/`ChatViewModel` on desktop and the equivalent `lp_chat_sessions_<uid>` logic
   on web.
6. **(New scoped work, largest, do last)** Design and build project/work-history cloud sync. Needs
   its own dedicated design session first (storage strategy for real `.lasero` files, versioning,
   size limits) — explicitly not something to fold into this pass.
7. **(Future, optional, after 1-6 are proven stable)** Decide whether to ever enforce
   `MaxDevices`/`ActivatedDevices` for new activations (never retroactively for grandfathered users),
   and whether `WebAccess`/`DesktopAccess` should ever diverge (e.g. desktop as a paid upsell). Not
   part of this unification effort unless a future business decision requires it.

---

## Phase 1 implementation status (2026-09-10)

Implemented exactly items 1-2 from §15's recommended order, per explicit approval. Nothing else in
this audit has been implemented — items 3-7 remain queued.

### 1. WPF — `MaterialPresetStore` UID-scoped local cache

Files: `Lasero.App/MaterialPresetStore.cs`, `Lasero.App/AccountScopedStorage.cs` (new, shared
SHA256(uid) filename helper also now used by `ChatStore`), `Lasero.App/ViewModels/MaterialsViewModel.cs`
(new `ReloadForAccount()`), `Lasero.App/ViewModels/MainViewModel.cs` (new
`ReloadAccountScopedCachesForCurrentAccount()`), `Lasero.App/App.xaml.cs` and
`Lasero.App/MainWindow.xaml.cs` (call the new reload method at both places an account can become
active: startup sign-in/session-resume, and the sign-out→sign-in-again flow in Settings).

**Migration behavior chosen**: no automatic migration. The pre-scoping shared `materials.json` is
left on disk untouched, forever — never read or written again once `SwitchAccount` has run for a real
UID. A freshly-scoped account starts with an empty local cache; the next successful `Synchronize()`
repopulates it from the cloud (`sync-materials`), which remains authoritative. This was a deliberate
choice over a "migrate to the first account seen" heuristic — the latter still amounts to a guess, and
the audit's own instruction was "otherwise do not silently assign a legacy cache to an arbitrary
signed-in account."

### 2. WPF — `RecentProjectsStore` UID-scoped local cache

File: `Lasero.App/RecentProjectsStore.cs`. Same `SwitchAccount(userId)` pattern and same no-migration
policy as above. No cloud sync exists for this data, so a freshly-scoped account's recent list starts
empty and fills back in as they open/save projects — this was flagged and accepted as expected
behavior, not a regression. Project files themselves were never moved or touched — only this index.

### 3. Web — `gemini.js` Apple entitlement fix

File: `netlify/functions/gemini.js`. Added the missing `appleActive` OR-term
(`stripeActive || licenseActive || playActive || appleActive`), mirroring `check-premium.js`'s
already-correct logic exactly. Pure 4-line additive diff, no other file touched, not committed.

**Important nuance found during final review, independently confirmed**: `isAllowed`
(`paidPremium || trialActive`) is currently **not used to gate the Gemini request at all** — the
actual access check was already disabled elsewhere (`// Rate limiting removed — all authenticated
users can use Gemini`, `gemini.js:90`); `isAllowed` is only read once, to opportunistically clear a
stale rate-limit counter. So today, no authenticated user — Apple-subscribed or otherwise — is
actually denied chat access by this function, meaning the fix is correct and worth keeping (it now
matches `check-premium.js`, and matters the moment gating is ever restored) but does not change any
current production behavior. If an Apple-user chat-access complaint is what motivated finding this bug,
the real cause is that the gate itself is dormant, not (only) the missing Apple term.

### Tests added

`Lasero.Tests/MaterialPresetStoreTests.cs`: 6 new tests (`SwitchAccount_IsolatesTwoDistinctAccountsFromEachOther`,
`SwitchAccount_ReloadsFromDiskAcrossStoreInstances`, `SwitchAccount_DoesNotCarrySyncStateFromPreviousAccount`,
`SwitchAccount_FiresChangedSoSubscribersRefresh`, `SwitchAccount_NullOrEmptyUserId_ClearsStateWithoutThrowing`
(now a `Theory` over `null`/`""`/`"   "`), `SwitchAccount_NeverReadsOrWritesTheLegacySharedFile`).
`Lasero.Tests/RecentProjectsStoreTests.cs`: new file, 9 tests (same coverage shape, plus baseline
`FirstRun`/`Touch persists`/`CorruptFile` cases that store had no prior test file for at all).
Web repo has no practical automated-test harness reachable without scaffolding new mocking
infrastructure for Netlify Blobs/Firebase — verified by manual trace of all 7 entitlement-source
combinations instead (documented in the implementing agent's report; not reproduced here).

### Final build/test result

```text
dotnet build LaseroDesktop.sln -c Debug                    0 warnings, 0 errors
dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug       490/490 (472 baseline + 18 new)
```
`Lasero.Avalonia.Tests` (frozen, out of scope) showed 2 pre-existing flaky failures at the very start
of this session, 0 on every rebuild since — consistent with flakiness already logged in prior HANDOFF
sessions, not caused by or fixed by this work.

### Review performed

Two independent agent review passes (regression/test-coverage review, then a separate final
adversarial review covering UID isolation, migration safety, sign-in/out transitions, null handling,
filesystem safety, and entitlement regression risk) both returned clean verdicts — WPF and web both
**SAFE TO SHIP AS-IS**. One latent (non-blocking, not fixed — out of the approved file list)
observation from review: `AccountViewModel.SignOut()` itself does not call `SwitchAccount(null)` —
today's UI flow is safe only because the window is hidden before a new `LoginWindow` resolves and
nothing rebinds until it succeeds. Worth a one-line hardening note if that flow is ever restructured.

### Known remaining phases (unchanged from §15, not started)

3. Grandfathering policy — confirm in writing (no code).
4. Passive device-activation logging (additive only).
5. `sync-chat-history` (new scoped work, mirrors `sync-materials`).
6. Project/work-history cloud sync (largest, needs its own design pass).
7. `MaxDevices`/`WebAccess`/`DesktopAccess` enforcement decision — future/optional.

---

## Phase 2 implementation status (2026-09-10)

Implemented §15 items 3-4 (grandfathering policy confirmed, passive device-activation logging), plus
the lifecycle-coupling fix Phase 1's own review flagged as a latent risk. Items 5-7 remain queued,
unchanged.

### 1. Lifecycle coupling fixed — account-scoped stores no longer depend on window ordering

Phase 1's known risk (`AccountViewModel.SignOut()` didn't itself clear stores; correctness depended on
`MainWindow`'s hide/show sequencing) is closed. `ChatStore`/`MaterialPresetStore`/`RecentProjectsStore`
now switch/clear automatically off a single mechanism: each owning ViewModel
(`ChatViewModel`/`MaterialsViewModel`/`HomeViewModel`) subscribes to the **already-existing**
`AccountViewModel.PropertyChanged` event and reloads on `UserId` changes — no new event bus, no
polling, no dependency on any window being shown, hidden, or reopened.

Files: `Lasero.App/ViewModels/ChatViewModel.cs`, `MaterialsViewModel.cs`, `HomeViewModel.cs` (gained an
`AccountViewModel` dependency it didn't have before, to own `RecentProjectsStore.SwitchAccount`
directly), `MainViewModel.cs` (removed the now-redundant `ReloadAccountScopedCachesForCurrentAccount()`
coordinator), `App.xaml.cs`/`MainWindow.xaml.cs` (removed the manual reload call sites — the automatic
subscription path, wired during DI construction before any sign-in/session-resume ever runs, is now
the only mechanism).

**Signed-out behavior** (`SwitchAccount(null)` on all three stores, unchanged from Phase 1, now
reliably triggered): clears in-memory state immediately, never reads/writes the legacy shared file,
never deletes any account's on-disk data, safe to call repeatedly (idempotent once already clear).

Tests: `Lasero.Tests/AccountScopedViewModelLifecycleTests.cs` (new, 18 tests) — for each of Chat/
Materials/Home: sign-in loads that account's data and sign-out clears it; sign-out→sign-in-as-different-
account has no bleed; direct account-to-account switch (no explicit sign-out) has no bleed; repeated
sign-out stays empty without throwing; empty/whitespace UserId is treated as signed-out. Simulates
"signed in as X" by setting `AccountViewModel.UserId` directly (no test server to sign in against) but
uses the real `SignOutCommand` wherever sign-out semantics matter.

### 2. Grandfathering policy — formally documented

See §11a above (new subsection under §11). No code changes — the existing `LaseroAccountClient`/
`AccountViewModel` stack already implements this policy; §11a documents it as approved, explicit, and
binding until a future decision changes it.

### 3. Passive device-activation logging — implemented, zero enforcement

**Device ID (desktop)**: `Lasero.Core/LaseroApi/DeviceIdStore.cs` (new) — a random `Guid.NewGuid()`
generated once per install, persisted at `%LocalAppData%\Lasero\device-id.txt` (atomic write, same
temp-then-move pattern as every other local store in this codebase), cached in memory after first
read. Explicitly NOT derived from machine name, username, MAC address, or any hardware serial —
verified by a dedicated test (`DeviceIdStoreTests.Id_IsNotDerivedFromMachineOrUserIdentity`).

**Backend endpoint**: `netlify/functions/register-device.js` (new, web repo) — POST only, reuses
`sync-materials.js`'s exact proven auth pattern (`_shared/firebase-auth.js`'s `requireFirebaseUser`,
Bearer Firebase ID token verified server-side; the uid used for storage comes **only** from the
verified token, never from the request body — a spoofed `uid`/`userId` field in the body is silently
ignored). Validates `deviceId` (UUID shape), `platform` (`web`/`desktop`/`android`/`ios` enum),
`appVersion` (optional, length-capped).

**Storage key strategy**: new Netlify Blobs store `lasero-device-activations`, keyed by the composite
string `${uid}/${deviceId}` — a direct key lookup (one `get`, one `setJSON`), never a scan, so it
never needs to enumerate the store to find or update one record. First call for a given uid+deviceId
creates `{deviceId, platform, appVersion, firstSeenAt, lastSeenAt}` with both timestamps equal;
every subsequent call updates only `lastSeenAt` (and `platform`/`appVersion` if they changed),
`firstSeenAt` is never rewritten.

**Desktop call sites**: `AccountViewModel.RegisterDeviceActivationAsync()`, fired (never awaited —
`_ = RegisterDeviceActivationAsync();`) immediately after `IsSignedIn = true` in both `SignIn()` and
`TryResumeSessionAsync()`'s success branch. Best-effort by design: wrapped in its own try/catch,
6-second timeout, any failure (offline, 5xx, timeout, thrown exception) is logged at Debug level and
swallowed — never surfaced to the user, never retried, never affects `IsSignedIn`/`IsPremium`/sign-in
success. Skipped entirely when offline (`if (_session is null || IsOffline) return;`, matching the
existing `RefreshEntitlementAsync` guard pattern).

**No enforcement policy — confirmed at every layer**: `register-device.js` contains no count, list, or
comparison logic of any kind — it is a single get + single set + `{ok:true}` response. No
`MaxDevices`/`ActivatedDevices` field exists anywhere. No device-management or device-removal UI was
added. Nothing in this phase can deny, warn about, or slow-walk a legitimate sign-in based on device
count.

**Tests**: `Lasero.Tests/DeviceIdStoreTests.cs` (5, new install/repeat-call/persistence/corrupt-file/
non-identifying), `Lasero.Tests/DeviceActivationClientTests.cs` (2, Bearer header + body shape, throws
on server error), `Lasero.Tests/AccountDeviceActivationTests.cs` (6, new — signed-in account's own id
token is sent; stable deviceId across calls; two different accounts register independently under their
own tokens; sign-out never triggers a call; activation failure/thrown-exception never fails sign-in).
Two pre-existing test files (`KamilAssistantViewModelTests.cs`, `MaterialSwatchViewModelTests.cs`)
needed a mechanical update for `AccountViewModel`'s two new constructor parameters — no behavior change
in either file. Backend has no practical automated-test harness in this repo (confirmed, not built from
scratch per the phase's own instruction) — verified instead via `node --check` plus a full manual trace
of 8 cases (missing/invalid auth, missing/invalid fields, first-call vs repeat-call timestamp behavior,
spoofed-uid rejection, two-uid same-deviceId isolation) — see the implementing session's own report for
the full trace.

### Final build/test result

```text
dotnet build LaseroDesktop.sln -c Debug                    0 warnings, 0 errors
dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug       521/521 (490 Phase-1-end baseline + 31 new)
```
One incidental observation, not a regression: the very first full-solution rebuild after this phase's
new `Lasero.Core` files were added caused `Lasero.Avalonia`/`Lasero.Avalonia.Tests` (frozen, untouched)
to recompile and surface ~23 pre-existing warnings that incremental-build caching had been hiding since
Phase 1; a second rebuild returned to a clean, stable 0/0 and has stayed there. Nothing in
`Lasero.Avalonia` was touched.

### Review performed

Two independent agent review passes — a privacy/security review (UID derivation, deviceId
non-identifiability, no hardware fingerprinting, no unauthenticated/cross-user writes, no
entitlement/billing coupling, best-effort/non-blocking guarantee) and a final regression review
(sign-out-clears-everything trace, no double-reload, no dead code, DI resolution, fire-and-forget
exception safety, test-quality spot-checks via mental revert, full build/test re-run) — both returned
**SAFE TO SHIP AS-IS** for both repos, no fixes required. Both agents independently re-verified the
build/test numbers themselves rather than trusting this document.

One structural observation from the regression review, not a defect: `OnAccountPropertyChanged`
handlers for a given `AccountViewModel` instance run as one C# multicast delegate per subscriber, so if
one subscriber's handler ever threw, later-registered subscribers for that same event would be skipped
for that invocation. No live path to this today (every store's load path is already defensively
try/caught), but worth knowing if a future subscriber is added without the same care.

### Known limitations / not live-verified

- No manual/live GUI verification was performed for either goal — correctness rests on code tracing,
  unit tests, and two independent agent reviews (regression + privacy/security), consistent with the
  standing instruction not to drive the live app unsupervised.
- `register-device.js` has no automated test harness backing it (see above) — its correctness rests on
  a manual trace, not executable tests, same limitation Phase 1's web-side fix had.
- The device-activation call currently only fires for the `desktop` platform; `web`/`android`/`ios`
  remain unimplemented client-side (explicitly out of scope for this phase — the backend enum already
  accepts them for when that work happens).

### Next phase (unchanged, queued)

5. `sync-chat-history` (new scoped work, mirrors `sync-materials`'s proven pattern).
6. Project/work-history cloud sync (largest remaining piece, needs its own design pass first).
7. `MaxDevices`/`WebAccess`/`DesktopAccess` enforcement decision — future/optional, only once real
   device-activation usage data (now being collected) justifies a decision either way.
