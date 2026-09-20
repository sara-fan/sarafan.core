# Store catalogue API

Store Ops includes `officialUrlRules`: `maximumLength`, `topLevelDomainListVersion` and `topLevelDomains`, using the same DTO as order URL rules. Store create/update normalizes protocol-free addresses to HTTPS, preserves explicit HTTP(S), and validates DNS hosts against the current IANA catalogue. Internationalized domains are canonicalized; IP addresses, credentials, unsupported schemes, control characters and backslashes are rejected with `invalid_store_url` associated with `officialUrl`. The limit applies to the canonical URL as well as the submitted address. Existing stored addresses remain readable for repair. Missing IANA data returns `503 tld_catalog_unavailable` from Ops/create/update; catalogue, detail, delete and logo endpoints remain independent of it.

## Public reads

| Method and path | Result |
|---|---|
| GET `/api/v1/stores?sort=recommended&search=shop` | All matching Active and Priority stores |
| GET `/api/v1/stores/featured` | Priority stores (at most six) |
| GET `/api/v1/stores/{id}/logo?v={digest}` | Current logo, only while the store is Active or Priority |

Both lists return `{ "items": [...] }`. Items have exactly `id`, `name`, `description`, `officialUrl`, `logoUrl`; `logoUrl` is always a digest-addressed image URL. Successful empty lists contain `items: []`. Legacy records without images remain available to staff for repair but are excluded from public responses. No login, consent, exchange-rate data or IANA catalogue is required.

The full-list `sort` values are `recommended` (default), `name-asc` and `name-desc`. Explicit empty, duplicate and unsupported sort values are rejected. Recommended and featured order is ascending `DisplayOrder`, then ascending `Id`. Alphabetical order is case-insensitive `ru-RU`, supports Latin and Cyrillic names, and preserves ascending ID ties in either direction. Core performs the sort; clients preserve the response order. Featured selection is independent of customer sorting.

The optional `search` parameter is trimmed, limited to 200 characters and matched case-insensitively against the store name only, including Latin and Cyrillic text. `%`, `_` and `\` are literal characters rather than wildcards. Empty or whitespace-only search returns the unfiltered catalogue. Duplicate and overlong search values are rejected with `invalid_store_search`. Search is applied before the selected sort; the featured endpoint does not accept search.

Public responses use `Cache-Control: no-cache`. Custom-image responses have a content-digest ETag and `X-Content-Type-Options: nosniff`. The `v` query parameter changes the image URL when bytes change; it is not a historical-image selector. Core checks current visibility even when `If-None-Match` is supplied. Hidden/missing custom images return 404, never 304. Catalogue changes are visible on the next request without frontend deployment.

## Staff reads and permissions

All routes below use separate staff authentication under `/api/v1/backoffice/stores` and `Cache-Control: no-store`.

| Method and suffix | Allowed roles | Result |
|---|---|---|
| GET base route | All staff | `{ "items": [...] }`, including hidden stores; optional numeric `status=0`, `status=1` or `status=2` |
| GET `/ops` | All staff | Status catalogue, limits and caller actions |
| GET `/{id}` | All staff | Store details |
| GET `/{id}/logo` | All staff | Logo preview, including hidden stores |
| POST base route | Administrator | Create; 201, details and Location |
| PUT `/{id}` | Administrator, Shift manager | Replace editable fields and optional logo; 200 and details |
| DELETE `/{id}` | Administrator | Permanently delete store and logo; 204 |

Staff list/detail items contain `id`, `name`, `description`, `officialUrl`, numeric `status`, `displayOrder`, UTC `createdAt`/`updatedAt`, UUID `version`, and nullable staff-only `logoUrl`. Lists project metadata without loading image bytes. Missing stores return 404.

Operations contain:

- `statuses`: `{value:0, name:"Скрыт", routeAlias:"hidden"}`, `{value:1, name:"Показывается в общем списке", routeAlias:"active"}`, `{value:2, name:"Показывается в общем списке и на главной странице", routeAlias:"priority"}`. Clients use these Core-owned names.
- `limits`: `maxPriorityStores=6`, `nameMaxLength=200`, `descriptionMaxLength=160`, `descriptionRecommendedLength=140`, `officialUrlMaxLength=2048`, `logoMaxBytes=2097152`, `logoContentTypes=["image/png","image/jpeg","image/webp"]`.
- Image resource limits: `logoMaxDimension=4096`, `logoMaxPixels=4194304`, `logoMaxFrames=100`, `logoMaxAnimationPixels=16777216`, `logoMaxMetadataBytes=1048576`. Clients display these server-owned limits alongside upload guidance.
- `actions`: booleans `view`, `create`, `edit`, `delete` for the current staff roles. These are UI capabilities; server authorization remains authoritative.

## Writes

POST and PUT consume `multipart/form-data` with fields `name`, `description`, `officialUrl`, `status`, `displayOrder`, uploaded `logo`, and (PUT only, required) UUID `version` from the last detail response. Defaults are Hidden status, order zero. PUT is a full field replacement, not a patch; clients send all editable fields. Creation always requires `logo`; update may omit it only to retain an existing image. Back Office may generate the image from the store name before submitting the same multipart field.

Name, description and URL are required and trimmed at their outer boundaries. Internal whitespace/case in names and descriptions is preserved. Description guidance at 140 characters is not a rejection threshold. Display order is a nonnegative integer, unique across every store including Hidden drafts. URLs default to HTTPS when the protocol is omitted and must have an IANA-listed DNS suffix, contain no credentials, controls or backslashes, and fit the published length after normalization. Core does not fetch store URLs or remote logos; staff verifies the official destination.

Every status requires an image. Staff can upload one or use Back Office to generate a uniform Sarafan image containing the store name only. Submitted images must be nonempty PNG/JPEG/WebP, match their declared content type and image structure checks, and not exceed 2 MiB. Validation checks bounded chunks/segments, PNG CRCs and image data, JPEG frame dimensions and scan data, and WebP frames (including lossless, extended and animated containers). It does not allocate a decompressed raster. The multipart request limit includes an additional 64 KiB for fields/framing. No SVG, external-image URL or uploaded filename is persisted.

Each image/canvas is bounded by both 4096 pixels per side and 4194304 total pixels. Animated WebP additionally requires ordered VP8X/ANIM/ANMF structures, frames contained in the declared canvas with matching encoded dimensions, at most 100 frames, and at most 16777216 canvas-pixels summed across frames. Count the full canvas per frame, including partial frames. PNG is static (APNG is rejected); its header combinations, palettes, CRCs and exact decompressed scanlines are validated, including Adam7. Compressed PNG metadata is limited to 1048576 decoded bytes in aggregate. JPEG/WebP entropy streams are not fully decoded; these checks bound resources and validate containers/headers without promising full codec verification. Invalid resources/structure use `invalid_store_logo_content`.

Creation initializes the entire aggregate with equal `createdAt` and `updatedAt`; subsequent writes advance timestamp/version. Public catalogue queries include only Active and Priority rows with images before ordering and the featured limit.

DELETE consumes JSON `{ "version": "<UUID from detail>" }`. Missing/empty versions are rejected. Field/status/order and logo mutations advance the parent version and timestamp; a stale update/delete returns 409 `store_update_conflict`. Clients retain drafts and require explicit refresh rather than retrying with a newer version automatically. All store writes acquire the catalogue PostgreSQL transaction advisory lock before reading mutable state and checking placement. Fields and logo commit in that transaction. The unique display-order index also rejects conflicting writes. At most six stores may have Priority status; updates exclude their own record. Priority does not outrank Active in catalogue sorting. Catalogue changes never update existing order product snapshots.

## Errors

Errors follow the central Russian RFC 9457 contract (`application/problem+json`, stable `type`, matching `code` and HTTP/body status). Field-specific types let clients map errors without parsing localized text:

| Code | Field/action |
|---|---|
| `invalid_store_sort` | Catalogue sort |
| `invalid_store_search` | Catalogue name search |
| `invalid_store_name` | Name |
| `invalid_store_description` | Description |
| `invalid_store_url` | Official URL |
| `invalid_store_status` | Status |
| `invalid_store_display_order` | Display order |
| `store_display_order_conflict` | `displayOrder` field, duplicate across any status (409) |
| `store_priority_limit_exceeded` | `status` field, seventh Priority store (409) |
| `invalid_store_version` | Reload before update/delete |
| `store_update_conflict` | Reload and reconcile edits (409) |
| `store_logo_required` | `logo` field; upload or generate an image |
| `invalid_store_logo_size` | Upload length |
| `invalid_store_logo_type` | Upload media type |
| `invalid_store_logo_content` | Upload content |

Malformed bindings use `validation_failed` with structured field errors. Standard authentication, authorization, missing-resource, request-size and unsupported-media errors retain their existing contracts. Diagnostic summaries redact store fields, uploaded content/metadata and version tokens.
