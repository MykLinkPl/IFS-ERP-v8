# IFS VAT / Tax Number Checker (Oracle SQL)

Validates **checksum and format** of VAT/tax numbers and returns a list of **invalid / attention-needed** records.
Targeted for IFS ERP v8 data.

## Supported countries (checksum rules)
`PL, GB, XI (Northern Ireland), DE, SK, IT, DK, FI, SE, FR, NL, BE`

## What it does
- Normalizes raw numbers (uppercase, strips non-alphanumerics, auto‑prefixes ISO2 where safe).
- Rejects numbers where a 2‑letter prefix **does not match** the record’s country code.
- Runs per‑country checksum/algorithm for the list above.
- Outputs a clean report with:
  - party_id, party_name, party_type, country, country_code
  - raw/cleaned/normalized numbers
  - validation_status (e.g., `VAT_PL_OK`, `VAT_CHECKSUM_FAIL`, `NOT_VAT_LIKE`)
  - validation_detail and `needs_attention` flag

## Requirements
- Oracle Database 12c+
- Tables: `customer_info(customer_id, name, association_no, country)` and `supplier_info(supplier_id, name, association_no, country)`.

## Run
1. Execute the SQL script from this folder in SQL*Plus/SQLcl/SQL Developer.
2. The final SELECT returns **only records that need attention** (invalid/mismatched format).

> Note: This is **offline** validation (no VIES/API calls).

## License
feel free to use :)
