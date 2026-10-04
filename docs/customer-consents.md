# Versioned customer consent

Governing requirements: FR-LEGAL-017, FR-LEGAL-020 and FR-LEGAL-021 at
[current LEGAL specification](https://github.com/sara-fan/sarafan/blob/main/docs/features/012-legal-documents-and-support/spec.md)
and [AUTH FR-AUTH-013](https://github.com/sara-fan/sarafan/blob/main/docs/features/004-authentication/spec.md). Historical reconciliation is tracked in [implementation issue #11](https://github.com/sara-fan/sarafan/issues/11)
and [Russian specification clarification #36](https://github.com/kuznetsovrm/sarafan/issues/36).

## Current acceptance and renewal

Legal kinds remain PersonalDataConsent=1 and UserAgreement=2; 0, 3 and 4 are reserved.
Registration/login acceptance of the currently effective agreement satisfies order creation.
A session remains usable when a newer document becomes effective. Before the next order,
the customer explicitly accepts any missing/outdated required version in the active session,
without another phone/SMS confirmation and without losing the entered draft.
A future publication does not require acceptance before its effective boundary.

GET /api/v1/consents/me returns a status for each supported kind, the newest 200
history records, and the earliest future effective boundary for either kind.
Status calculation is independent of the history limit.

Authenticated POST /api/v1/consents/me/user-agreement accepts
documentId, contentHash, decision: "grant" and a nonempty UUID idempotencyKey.
No phone/code request is required. It returns the customer consent envelope.
Personal-data grant/refusal remains at POST /api/v1/consents/me/personal-data.

Both endpoints validate the exact current version and revalidate newly recorded decisions
after persistence before commit. Identical subject-scoped retries return current status
without adding evidence, even if their document is now outdated. Reusing a key with
different content returns 409 consent_conflict; stale new confirmations return
409 consent_version_changed.

New-order writes require both current grants in the consent transaction,
including revalidation after persistence before commit. Missing/outdated agreement
acceptance returns 409 user_agreement_required; personal-data absence returns
409 personal_data_consent_required. Missing effective required documents return
409 consent_document_unavailable. Profile/photo writes keep the personal-data-only gate,
including the pre-binding multipart guard. Checkout also keeps both current grants.
Reads, logout and photo deletion retain their existing access rules.

## Evidence retention

All versioned decisions for kinds 1 and 2 are retained indefinitely, including refusals,
historical withdrawal decisions and disabled accounts. Immutable document IDs, source,
HTML and digests remain stable. Existing retired records are not modified.

The existing RetainUntil column and Consents:EvidenceDays option are legacy compatibility
metadata and do not authorize removal of supported decision evidence. No migration is
needed. The worker continues expiry of temporary onboarding receipts and removes old
replay markers only after document supersession. Previously disposed decisions are not
reconstructed. A retried historical grant cannot revive permission after a newer refusal.

Withdrawal requests remain independent manual queue records; creating or processing one
does not create a withdrawal decision or alter customer permissions or account state.

## Verification and rollout

Ship Core and customer UI together for the agreement status/renewal contract.
Use existing application logging and safe worker diagnostics. No database repair,
bootstrap or data conversion is required. Automated checks use isolated InMemory stores,
disable migrations/background jobs and never access the protected local database.