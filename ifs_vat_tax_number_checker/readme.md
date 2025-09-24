# IFS VAT / Tax Number Checker (Oracle SQL)

Validates **checksum and format** of VAT/tax numbers and returns a list of **invalid / attention‑needed** records for IFS ERP v8 data.

## Author
[MykLink \| Smart Connections \| Przemysław Myk](https://myklink.pl/)

## Supported countries (checksum rules)
**PL, GB, XI (Northern Ireland), DE, SK, IT, DK, FI, SE, FR, NL, BE**

## Files
| File | Purpose | Scope | SEO (minimal) |
|---|---|---|---|
| `multiple_countries_tax_number_validation.sql` | Unified validator for multiple countries. Outputs **only invalid/attention** items with status and details. | Countries above (PL/GB/XI/DE/SK/IT/DK/FI/SE/FR/NL/BE) | oracle sql vat validation, multi‑country, checksum rules |
| `pl_nip_vat_invalid_identities.sql` | Stand‑alone validator for **Poland** (NIP). Shows **only invalid** Polish entries. | PL | polish NIP check, PL VAT checksum, oracle script |
| `uk_vat_invalid_identities.sql` | Stand‑alone validator for **United Kingdom** (GB/XI). Shows **only invalid** UK entries. | GB, XI | UK VAT mod‑97, GB XI validation, oracle sql |

## What it does
- Normalizes raw numbers (uppercase, strips non‑alphanumerics, safe ISO2 auto‑prefix).
- Rejects numbers where a 2‑letter prefix **doesn’t match** the country code.
- Runs country‑specific checksum/algorithm for the supported list above.
- Produces a clear report with columns like: party_id, party_name, country_code, raw/cleaned/normalized numbers, `validation_status`, `validation_detail`, `needs_attention`.

## Requirements
- **Oracle Database 11g or newer**.
- Input tables: `customer_info(customer_id, name, association_no, country)` and `supplier_info(supplier_id, name, association_no, country)`.

## Run
Execute the chosen `.sql` in SQL*Plus/SQLcl/SQL Developer.  
The final query returns only **invalid or ambiguous** entries.

> Note: Offline validation (no VIES/API calls).

## License
feel free to use :)
