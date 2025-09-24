WITH unified AS (
  SELECT
    TO_CHAR(a.customer_id)   AS party_id,
    a.name                   AS party_name,
    'CUSTOMER'               AS party_type,
    a.association_no         AS raw_association_no,
    a.country
  FROM customer_info a
  WHERE a.country = 'UNITED KINGDOM' AND a.association_no IS NOT NULL

  UNION ALL

  SELECT
    TO_CHAR(s.supplier_id)   AS party_id,
    s.name                   AS party_name,
    'SUPPLIER'               AS party_type,
    s.association_no         AS raw_association_no,
    s.country
  FROM supplier_info s
  WHERE s.country = 'UNITED KINGDOM' AND s.association_no IS NOT NULL
),
norm AS (
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
  -- WYKLUCZ: zaczyna się od 2 liter i to nie jest 'GB'
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    cleaned
  FROM norm
  WHERE NOT ( REGEXP_LIKE(cleaned, '^[A-Z]{2}')
              AND SUBSTR(cleaned, 1, 2) <> 'GB' )
),
prefixed AS (
  -- Dodaj 'GB' dla gołych cyfr: 9/12 (VAT) lub 8 (specjalny przypadek)
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    cleaned,
    CASE
      WHEN REGEXP_LIKE(cleaned, '^(GB|XI)') THEN cleaned
      WHEN REGEXP_LIKE(cleaned, '^[0-9]{9}([0-9]{3})?$') THEN 'GB' || cleaned
      WHEN REGEXP_LIKE(cleaned, '^[0-9]{8}$') THEN 'GB' || cleaned
      ELSE cleaned
    END AS normalized
  FROM filtered
),
vat_core AS (
  -- 9 cyfr do checksumy; wykryj 12 cyfr (oddział) i 8-cyfrowe przypadki
  SELECT
    party_id,
    party_name,
    party_type,
    country,
    raw_association_no,
    normalized,
    CASE
      WHEN REGEXP_LIKE(normalized, '^(GB|XI)[0-9]{9}') THEN
           REGEXP_SUBSTR(normalized, '^(GB|XI)([0-9]{9})', 1, 1, NULL, 2)
      WHEN REGEXP_LIKE(normalized, '^[0-9]{9}$') THEN
           normalized
      ELSE
           NULL
    END AS vat9,
    CASE
      WHEN REGEXP_LIKE(normalized, '^(GB|XI)[0-9]{12}$')
        OR REGEXP_LIKE(normalized, '^[0-9]{12}$')
      THEN 'Y' ELSE 'N'
    END AS has_branch_code,
    CASE WHEN REGEXP_LIKE(normalized, '^[0-9]{8}$') THEN 'Y' ELSE 'N' END AS was_len8_only_digits
  FROM prefixed
),
check_calc AS (
  /* Walidacja UK VAT (9 cyfr):
     - wagi: 8,7,6,5,4,3,2,10,1
     - TEST 1: MOD(sum, 97) = 0
     - TEST 2: MOD(sum + 55, 97) = 0 (nowsze pule)
  */
  SELECT
    v.*,
    CASE
      WHEN vat9 IS NULL THEN NULL
      ELSE
        CASE
          WHEN (
            MOD( ( SELECT SUM( TO_NUMBER(SUBSTR(v.vat9, pos, 1)) * weight )
                   FROM (
                     SELECT 1 AS pos, 8  AS weight FROM DUAL UNION ALL
                     SELECT 2, 7 FROM DUAL UNION ALL
                     SELECT 3, 6 FROM DUAL UNION ALL
                     SELECT 4, 5 FROM DUAL UNION ALL
                     SELECT 5, 4 FROM DUAL UNION ALL
                     SELECT 6, 3 FROM DUAL UNION ALL
                     SELECT 7, 2 FROM DUAL UNION ALL
                     SELECT 8,10 FROM DUAL UNION ALL
                     SELECT 9, 1 FROM DUAL
                   )
                 ), 97)
            = 0
            OR
            MOD( ( SELECT SUM( TO_NUMBER(SUBSTR(v.vat9, pos, 1)) * weight )
                   FROM (
                     SELECT 1 AS pos, 8  AS weight FROM DUAL UNION ALL
                     SELECT 2, 7 FROM DUAL UNION ALL
                     SELECT 3, 6 FROM DUAL UNION ALL
                     SELECT 4, 5 FROM DUAL UNION ALL
                     SELECT 5, 4 FROM DUAL UNION ALL
                     SELECT 6, 3 FROM DUAL UNION ALL
                     SELECT 7, 2 FROM DUAL UNION ALL
                     SELECT 8,10 FROM DUAL UNION ALL
                     SELECT 9, 1 FROM DUAL
                   )
                 ) + 55, 97)
            = 0
          ) THEN 'Y' ELSE 'N'
        END
    END AS vat9_checksum_ok
  FROM vat_core v
)
SELECT
  party_id,
  party_name,
  party_type,
  country,
  raw_association_no,
  normalized AS normalized_association_no,
  CASE
    WHEN vat9 IS NULL THEN 'NOT_VAT_LIKE'
    WHEN vat9_checksum_ok = 'Y' THEN
      CASE WHEN has_branch_code = 'Y' THEN 'VAT_GB_12_OK' ELSE 'VAT_GB_9_OK' END
    ELSE 'VAT_CHECKSUM_FAIL'
  END AS uk_vat_validation,
  CASE
    WHEN was_len8_only_digits = 'Y' THEN 'Y'
    WHEN vat9 IS NULL OR vat9_checksum_ok <> 'Y' THEN 'Y'
    ELSE 'N'
  END AS needs_attention
FROM check_calc
WHERE was_len8_only_digits = 'Y'
   OR vat9 IS NULL
   OR vat9_checksum_ok <> 'Y';
