# Staff list search contract

Staff `search` is a trimmed, case-insensitive literal substring of an individual displayed field. Ordinary spaces, NBSP and narrow NBSP are equivalent. Empty queries do not filter. `%`, `_` and backslashes are literal. Matching uses Russian labels, grouped comma-decimal money with currency symbols, `dd.MM.yyyy` dates, and `dd.MM.yyyy, HH:mm МСК` timestamps in Europe/Moscow. Hidden values, raw ISO/numeric alternatives and expanded-only evidence do not match.

| Endpoint under `/api/v1/backoffice` | Fields | Maximum characters |
| --- | --- | --- |
| `/orders` | Public number, status label, product/store names or displayed fallbacks, seller price, quantity, created/updated timestamps | 2048 |
| `/orders/{orderNumber}/history` | Collapsed timestamp, event label, actor plus current-name annotation, affected-area summary | 200 |
| `/legal-documents/audit` | Timestamp, action label, title, kind label, document ID, version, effective date, administrator name or displayed fallback | 200 |
| `/service-catalogue/audit` | Timestamp, action/service labels, actor, formatted before/after summaries | 200 |
| `/consents/withdrawal-requests` | `№ {customerId}`, processing-status label, requested timestamp | 200 |

The withdrawal queue accepts free text rather than digits only. Search combines with existing structured filters using AND. Match before filtered count, deterministic sort, offset and limit. Preserve response envelopes and trimmed search echoes; normalized matching text is not a new DTO property. Dedicated ID filters remain available even when hidden IDs no longer participate in free-text search.

Tariff search reads the displayed summary from saved snapshots, including methods, percentages, money, ordered bands and validity periods. It never searches raw JSON. History search does not load detail evidence. No schema migration is required; deploy Core before or together with back office.

Opt-in database checks: provision a disposable PostgreSQL database named `sarafan_search_checks` on loopback, then set `SARAFAN_SEARCH_TEST_DATABASE` explicitly and run `dotnet test --filter FullyQualifiedName~ListDisplaySearchTests`. The fixture applies migrations only to that disposable database and rolls test writes back. Without the variable PostgreSQL cases are skipped; the InMemory cases still run. Do not use application connection settings, user containers or persistent user storage.
