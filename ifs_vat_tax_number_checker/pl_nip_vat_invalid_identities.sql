-- Check if tax number is ok - report invalid numbers
-- Oracle 11g compatible
-- Author: Przemysław Myk — MykLink / Smart Connections
-- https://github.com/MykLinkPl

WITH unified AS (
  SELECT
    TO_CHAR(a.customer_id)   AS party_id,
    a.name                   AS party_name,
    'CUSTOMER'               AS party_type,
    a.association_no         AS raw_association_no,
    a.country
  FROM customer_info a
  WHERE a.country IN ('POLAND','POLSKA') AND a.association_no IS NOT NULL

  UNION ALL

  SELECT
    TO_CHAR(s.supplier_id)   AS party_id,
    s.name                   AS party_name,
    'SUPPLIER'               AS party_type,
    s.association_no         AS raw_association_no,
    s.country
  FROM supplier_info s
  WHERE s.country IN ('POLAND','POLSKA') AND s.association_no IS NOT NULL
),
norm AS (
  -- 1) Normalizacja tekstu
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    UPPER(REGEXP_REPLACE(raw_association_no, '[^A-Za-z0-9]', '')) AS cleaned
  FROM unified
),
filtered AS (
  -- 2) WYKLUCZ: zaczyna się od 2 liter i to nie jest 'PL'
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    cleaned
  FROM norm
  WHERE NOT ( REGEXP_LIKE(cleaned, '^[A-Z]{2}')
              AND SUBSTR(cleaned, 1, 2) <> 'PL' )
),
prefixed AS (
  -- 3) Dodaj 'PL' dla 10-cyfrowych numerów bez prefiksu
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    cleaned,
    CASE
      WHEN REGEXP_LIKE(cleaned, '^PL') THEN cleaned
      WHEN REGEXP_LIKE(cleaned, '^[0-9]{10}$') THEN 'PL' || cleaned
      ELSE cleaned
    END AS normalized
  FROM filtered
),
nip_core AS (
  -- 4) Wyciągnij 10-cyfrowy NIP do walidacji (po PL lub „goły” 10)
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    normalized,
    CASE
      WHEN REGEXP_LIKE(normalized, '^PL[0-9]{10}') THEN
           REGEXP_SUBSTR(normalized, '^(PL)([0-9]{10})', 1, 1, NULL, 2)
      WHEN REGEXP_LIKE(normalized, '^[0-9]{10}$') THEN
           normalized
      ELSE
           NULL
    END AS nip10
  FROM prefixed
),
check_calc AS (
  /* 5) Walidacja NIP:
        wagi: 6,5,7,2,3,4,5,6,7 (dla cyfr 1..9)
        kontrolna = cyfra 10; warunek: SUM(w_i * d_i) % 11 = d10 oraz d10 != 10
  */
  SELECT
    n.*,
    CASE
      WHEN nip10 IS NULL THEN NULL
      ELSE
        CASE
          WHEN (
            MOD( ( SELECT SUM( TO_NUMBER(SUBSTR(n.nip10, pos, 1)) * weight )
                   FROM (
                     SELECT 1 AS pos, 6 AS weight FROM DUAL UNION ALL
                     SELECT 2, 5 FROM DUAL UNION ALL
                     SELECT 3, 7 FROM DUAL UNION ALL
                     SELECT 4, 2 FROM DUAL UNION ALL
                     SELECT 5, 3 FROM DUAL UNION ALL
                     SELECT 6, 4 FROM DUAL UNION ALL
                     SELECT 7, 5 FROM DUAL UNION ALL
                     SELECT 8, 6 FROM DUAL UNION ALL
                     SELECT 9, 7 FROM DUAL
                   )
                 ), 11)
            = TO_NUMBER(SUBSTR(n.nip10, 10, 1))
            AND TO_NUMBER(SUBSTR(n.nip10, 10, 1)) <> 10
          )
          THEN 'Y' ELSE 'N'
        END
    END AS nip_checksum_ok
  FROM nip_core n
)
-- 6) Wynik
SELECT
  party_id,
  party_name,
  party_type,
  country,
  raw_association_no,
  normalized AS normalized_association_no,
  CASE
    WHEN nip10 IS NULL THEN 'NOT_VAT_LIKE'
    WHEN nip_checksum_ok = 'Y' THEN 'VAT_PL_OK'
    ELSE 'VAT_CHECKSUM_FAIL'
  END AS pl_vat_validation,
  CASE
    WHEN nip10 IS NULL OR nip_checksum_ok <> 'Y' THEN 'Y' ELSE 'N'
  END AS needs_attention
FROM check_calc
WHERE (nip10 IS NULL OR nip_checksum_ok <> 'Y')
and substr(party_id,0,1) not in ('P') --- specific type for our instance
;
