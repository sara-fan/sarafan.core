# Customer profile save

Pilot PROFILE FR-PROFILE-014 uses explicit Edit → Save/Cancel. The UI stages fields and photo changes in memory and sends a single authenticated request on Save.

- `PUT /api/v1/customers/me`, `application/json`: existing `CustomerProfileUpdateRequest`; photo unchanged.
- `PUT /api/v1/customers/me/profile`, `multipart/form-data`: the same optional profile fields, optional file `photo`, and optional boolean `removePhoto` (default false). Omitted/empty profile fields clear their saved values, matching JSON full-profile replacement semantics. Phone is never editable.
- A photo and `removePhoto=true` together return the standard `validation_failed` Problem Details with `errors.photo`.
- Photo validation matches the existing photo endpoint: JPEG, PNG or WebP, valid signature, nonempty and at most 5 MiB. Multipart limits allow 64 KiB overhead.
- Current Personal Data Consent is checked before multipart binding, and again through the existing transaction/version checks. Fields and the photo operation share the customer lock and transaction. Validation failure leaves both unchanged.
- Success returns the standard `CustomerDto` with authoritative `profile`, `state`, `updatedAt` and `hasPhoto`.
- Existing per-photo upload/read/delete endpoints remain compatible and serialize mutations with profile saves through the same transaction and customer lock. Photo deletion is idempotent and remains available without current Personal Data Consent. The profile editor uses the combined save for mutations.
- Optional profile fields remain partially saveable. Existing saved order recipient/delivery snapshots are never rewritten by profile saves; subsequent checkout uses the current profile through its existing flow.

No database schema change or migration is required.
