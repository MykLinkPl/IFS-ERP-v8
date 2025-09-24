-- Check if tax number is ok - report invalid numbers
-- Oracle 11g compatible
-- Author: Przemysław Myk — MykLink / Smart Connections
-- https://github.com/MykLinkPl

WITH unified AS (
  SELECT TO_CHAR(a.customer_id) AS party_id,
         a.name                AS party_name,
         'CUSTOMER'            AS party_type,
         a.association_no      AS raw_association_no,
         UPPER(TRIM(a.country_db)) AS iso2
  FROM ifsapp.customer_info a
  WHERE a.association_no IS NOT NULL

  UNION ALL
  SELECT TO_CHAR(s.supplier_id) AS party_id,
         s.name                 AS party_name,
         'SUPPLIER'             AS party_type,
         s.association_no       AS raw_association_no,
         UPPER(TRIM(s.country_db)) AS iso2
  FROM supplier_info s
  WHERE s.association_no IS NOT NULL
),
norm AS (
  SELECT
    party_id, party_name, party_type, iso2,
    UPPER(REGEXP_REPLACE(raw_association_no, '[^A-Za-z0-9]', '')) AS cleaned,
    raw_association_no
  FROM unified
),
filtered AS (
  SELECT *
  FROM norm
  WHERE NOT (
    iso2 IS NOT NULL
    AND REGEXP_LIKE(cleaned, '^[A-Z]{2}')
    AND SUBSTR(cleaned,1,2) <> iso2
  )
),
prefixed AS (
  SELECT
    party_id, party_name, party_type, iso2, raw_association_no, cleaned,
    CASE
      WHEN iso2 IN ('GB','XI') THEN
        CASE
          WHEN REGEXP_LIKE(cleaned, '^(GB|XI)') THEN cleaned
          WHEN REGEXP_LIKE(cleaned, '^[0-9]{9}([0-9]{3})?$') THEN iso2 || cleaned
          WHEN REGEXP_LIKE(cleaned, '^[0-9]{8}$') THEN iso2 || cleaned
          ELSE cleaned
        END
      WHEN iso2 = 'PL' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^PL') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{10}$') THEN 'PL' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'DE' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^DE') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{9}$') THEN 'DE' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'SK' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^SK') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{10}$') THEN 'SK' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'IT' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^IT') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{11}$') THEN 'IT' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'DK' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^DK') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{8}$') THEN 'DK' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'FI' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^FI') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{8}$') THEN 'FI' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'SE' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^SE') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{10}01$') THEN 'SE' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'FR' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^FR') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{11}$') THEN 'FR' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'NL' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^NL') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{9}B[0-9]{2}$') THEN 'NL' || cleaned
             ELSE cleaned END
      WHEN iso2 = 'BE' THEN
        CASE WHEN REGEXP_LIKE(cleaned, '^BE') THEN cleaned
             WHEN REGEXP_LIKE(cleaned, '^[0-9]{10}$') THEN 'BE' || cleaned
             ELSE cleaned END
      ELSE cleaned
    END AS normalized
  FROM filtered
),
core AS (
  SELECT
    p.*,
    CASE WHEN iso2 IN ('GB','XI') AND REGEXP_LIKE(normalized, '^(GB|XI)[0-9]{9}([0-9]{3})?$')
         THEN REGEXP_SUBSTR(normalized, '^(GB|XI)([0-9]{9})', 1, 1, NULL, 2) END AS uk9,
    CASE WHEN iso2 = 'PL' AND REGEXP_LIKE(normalized, '^PL[0-9]{10}')
         THEN REGEXP_SUBSTR(normalized, '^(PL)([0-9]{10})', 1, 1, NULL, 2) END AS pl10,
    CASE WHEN iso2 = 'DE' AND REGEXP_LIKE(normalized, '^DE[0-9]{9}')
         THEN REGEXP_SUBSTR(normalized, '^(DE)([0-9]{9})', 1, 1, NULL, 2) END AS de9,
    CASE WHEN iso2 = 'SK' AND REGEXP_LIKE(normalized, '^SK[0-9]{10}')
         THEN REGEXP_SUBSTR(normalized, '^(SK)([0-9]{10})', 1, 1, NULL, 2) END AS sk10,
    CASE WHEN iso2 = 'IT' AND REGEXP_LIKE(normalized, '^IT[0-9]{11}')
         THEN REGEXP_SUBSTR(normalized, '^(IT)([0-9]{11})', 1, 1, NULL, 2) END AS it11,
    CASE WHEN iso2 = 'DK' AND REGEXP_LIKE(normalized, '^DK[0-9]{8}')
         THEN REGEXP_SUBSTR(normalized, '^(DK)([0-9]{8})', 1, 1, NULL, 2) END AS dk8,
    CASE WHEN iso2 = 'FI' AND REGEXP_LIKE(normalized, '^FI[0-9]{8}')
         THEN REGEXP_SUBSTR(normalized, '^(FI)([0-9]{8})', 1, 1, NULL, 2) END AS fi8,
    CASE WHEN iso2 = 'SE' AND REGEXP_LIKE(normalized, '^SE[0-9]{10}01$')
         THEN REGEXP_SUBSTR(normalized, '^(SE)([0-9]{10})01$', 1, 1, NULL, 2) END AS se10,
    CASE WHEN iso2 = 'FR' AND REGEXP_LIKE(normalized, '^FR[0-9]{11}$')
         THEN REGEXP_SUBSTR(normalized, '^FR([0-9]{2})([0-9]{9})', 1, 1, NULL, 0) END AS fr_all,
    CASE WHEN iso2 = 'NL' AND REGEXP_LIKE(normalized, '^NL[0-9]{9}B[0-9]{2}$')
         THEN REGEXP_SUBSTR(normalized, '^NL([0-9]{9})B([0-9]{2})$', 1, 1, NULL, 1) END AS nl9,
    CASE WHEN iso2 = 'BE' AND REGEXP_LIKE(normalized, '^BE[0-9]{10}$')
         THEN REGEXP_SUBSTR(normalized, '^BE([0-9]{10})$', 1, 1, NULL, 1) END AS be10
  FROM prefixed p
),
iter (party_id, de9, pos, prod) AS (
  SELECT party_id, de9, 1, 10 FROM core WHERE de9 IS NOT NULL
  UNION ALL
  SELECT i.party_id, i.de9, i.pos+1,
         MOD( 2 * (CASE WHEN MOD( TO_NUMBER(SUBSTR(i.de9, i.pos, 1)) + i.prod, 10 ) = 0
                        THEN 10 ELSE MOD( TO_NUMBER(SUBSTR(i.de9, i.pos, 1)) + i.prod, 10 ) END), 11 )
  FROM iter i WHERE i.pos <= 8
),
calc AS (
  SELECT
    c.*,
    CASE WHEN pl10 IS NOT NULL AND
      MOD( (SELECT SUM(TO_NUMBER(SUBSTR(pl10,pos,1))*weight) FROM (
              SELECT 1 pos,6 weight FROM DUAL UNION ALL
              SELECT 2,5 FROM DUAL UNION ALL
              SELECT 3,7 FROM DUAL UNION ALL
              SELECT 4,2 FROM DUAL UNION ALL
              SELECT 5,3 FROM DUAL UNION ALL
              SELECT 6,4 FROM DUAL UNION ALL
              SELECT 7,5 FROM DUAL UNION ALL
              SELECT 8,6 FROM DUAL UNION ALL
              SELECT 9,7 FROM DUAL)), 11)
      = TO_NUMBER(SUBSTR(pl10,10,1))
      AND TO_NUMBER(SUBSTR(pl10,10,1)) <> 10
    THEN 'Y' ELSE CASE WHEN pl10 IS NULL THEN NULL ELSE 'N' END END AS pl_ok,

    CASE WHEN uk9 IS NOT NULL AND (
           MOD( (SELECT SUM(TO_NUMBER(SUBSTR(uk9,pos,1))*weight) FROM (
                 SELECT 1 pos,8 weight FROM DUAL UNION ALL
                 SELECT 2,7 FROM DUAL UNION ALL
                 SELECT 3,6 FROM DUAL UNION ALL
                 SELECT 4,5 FROM DUAL UNION ALL
                 SELECT 5,4 FROM DUAL UNION ALL
                 SELECT 6,3 FROM DUAL UNION ALL
                 SELECT 7,2 FROM DUAL UNION ALL
                 SELECT 8,10 FROM DUAL UNION ALL
                 SELECT 9,1 FROM DUAL)),97)=0
           OR MOD( (SELECT SUM(TO_NUMBER(SUBSTR(uk9,pos,1))*weight) FROM (
                 SELECT 1 pos,8 weight FROM DUAL UNION ALL
                 SELECT 2,7 FROM DUAL UNION ALL
                 SELECT 3,6 FROM DUAL UNION ALL
                 SELECT 4,5 FROM DUAL UNION ALL
                 SELECT 5,4 FROM DUAL UNION ALL
                 SELECT 6,3 FROM DUAL UNION ALL
                 SELECT 7,2 FROM DUAL UNION ALL
                 SELECT 8,10 FROM DUAL UNION ALL
                 SELECT 9,1 FROM DUAL)) + 55,97)=0 )
    THEN 'Y' ELSE CASE WHEN uk9 IS NULL THEN NULL ELSE 'N' END END AS gb_ok,

    CASE WHEN de9 IS NOT NULL AND (
         CASE WHEN (11 - (SELECT prod FROM iter i WHERE i.party_id=c.party_id AND i.de9=c.de9 AND i.pos=9)) IN (10,11)
              THEN 0
              ELSE 11 - (SELECT prod FROM iter i WHERE i.party_id=c.party_id AND i.de9=c.de9 AND i.pos=9)
         END ) = TO_NUMBER(SUBSTR(de9,9,1))
    THEN 'Y' ELSE CASE WHEN de9 IS NULL THEN NULL ELSE 'N' END END AS de_ok,

    CASE WHEN sk10 IS NOT NULL AND MOD(TO_NUMBER(sk10),11)=0
    THEN 'Y' ELSE CASE WHEN sk10 IS NULL THEN NULL ELSE 'N' END END AS sk_ok,

    CASE WHEN it11 IS NOT NULL AND (
        MOD(
          ( TO_NUMBER(SUBSTR(it11,1,1)) + TO_NUMBER(SUBSTR(it11,3,1)) +
            TO_NUMBER(SUBSTR(it11,5,1)) + TO_NUMBER(SUBSTR(it11,7,1)) +
            TO_NUMBER(SUBSTR(it11,9,1)) ) +
          ( (CASE TO_NUMBER(SUBSTR(it11,2,1)) WHEN 0 THEN 0 WHEN 1 THEN 2 WHEN 2 THEN 4 WHEN 3 THEN 6 WHEN 4 THEN 8 WHEN 5 THEN 1 WHEN 6 THEN 3 WHEN 7 THEN 5 WHEN 8 THEN 7 WHEN 9 THEN 9 END) +
            (CASE TO_NUMBER(SUBSTR(it11,4,1)) WHEN 0 THEN 0 WHEN 1 THEN 2 WHEN 2 THEN 4 WHEN 3 THEN 6 WHEN 4 THEN 8 WHEN 5 THEN 1 WHEN 6 THEN 3 WHEN 7 THEN 5 WHEN 8 THEN 7 WHEN 9 THEN 9 END) +
            (CASE TO_NUMBER(SUBSTR(it11,6,1)) WHEN 0 THEN 0 WHEN 1 THEN 2 WHEN 2 THEN 4 WHEN 3 THEN 6 WHEN 4 THEN 8 WHEN 5 THEN 1 WHEN 6 THEN 3 WHEN 7 THEN 5 WHEN 8 THEN 7 WHEN 9 THEN 9 END) +
            (CASE TO_NUMBER(SUBSTR(it11,8,1)) WHEN 0 THEN 0 WHEN 1 THEN 2 WHEN 2 THEN 4 WHEN 3 THEN 6 WHEN 4 THEN 8 WHEN 5 THEN 1 WHEN 6 THEN 3 WHEN 7 THEN 5 WHEN 8 THEN 7 WHEN 9 THEN 9 END) +
            (CASE TO_NUMBER(SUBSTR(it11,10,1)) WHEN 0 THEN 0 WHEN 1 THEN 2 WHEN 2 THEN 4 WHEN 3 THEN 6 WHEN 4 THEN 8 WHEN 5 THEN 1 WHEN 6 THEN 3 WHEN 7 THEN 5 WHEN 8 THEN 7 WHEN 9 THEN 9 END) )
        ,10) = TO_NUMBER(SUBSTR(it11,11,1))
      )
    THEN 'Y' ELSE CASE WHEN it11 IS NULL THEN NULL ELSE 'N' END END AS it_ok,

    CASE WHEN dk8 IS NOT NULL AND
      MOD( 2*TO_NUMBER(SUBSTR(dk8,1,1)) + 7*TO_NUMBER(SUBSTR(dk8,2,1)) +
           6*TO_NUMBER(SUBSTR(dk8,3,1)) + 5*TO_NUMBER(SUBSTR(dk8,4,1)) +
           4*TO_NUMBER(SUBSTR(dk8,5,1)) + 3*TO_NUMBER(SUBSTR(dk8,6,1)) +
           2*TO_NUMBER(SUBSTR(dk8,7,1)) + 1*TO_NUMBER(SUBSTR(dk8,8,1)), 11) = 0
    THEN 'Y' ELSE CASE WHEN dk8 IS NULL THEN NULL ELSE 'N' END END AS dk_ok,

    CASE WHEN fi8 IS NOT NULL THEN
      CASE
        WHEN MOD( 7*TO_NUMBER(SUBSTR(fi8,1,1)) + 9*TO_NUMBER(SUBSTR(fi8,2,1)) +
                  10*TO_NUMBER(SUBSTR(fi8,3,1)) + 5*TO_NUMBER(SUBSTR(fi8,4,1)) +
                  8*TO_NUMBER(SUBSTR(fi8,5,1)) + 4*TO_NUMBER(SUBSTR(fi8,6,1)) +
                  2*TO_NUMBER(SUBSTR(fi8,7,1)), 11) = 0
             AND TO_NUMBER(SUBSTR(fi8,8,1)) = 0 THEN 'Y'
        WHEN MOD( 7*TO_NUMBER(SUBSTR(fi8,1,1)) + 9*TO_NUMBER(SUBSTR(fi8,2,1)) +
                  10*TO_NUMBER(SUBSTR(fi8,3,1)) + 5*TO_NUMBER(SUBSTR(fi8,4,1)) +
                  8*TO_NUMBER(SUBSTR(fi8,5,1)) + 4*TO_NUMBER(SUBSTR(fi8,6,1)) +
                  2*TO_NUMBER(SUBSTR(fi8,7,1)), 11) = 1 THEN 'N'
        WHEN 11 - MOD( 7*TO_NUMBER(SUBSTR(fi8,1,1)) + 9*TO_NUMBER(SUBSTR(fi8,2,1)) +
                       10*TO_NUMBER(SUBSTR(fi8,3,1)) + 5*TO_NUMBER(SUBSTR(fi8,4,1)) +
                       8*TO_NUMBER(SUBSTR(fi8,5,1)) + 4*TO_NUMBER(SUBSTR(fi8,6,1)) +
                       2*TO_NUMBER(SUBSTR(fi8,7,1)), 11) = TO_NUMBER(SUBSTR(fi8,8,1)) THEN 'Y'
        ELSE 'N'
      END
    ELSE NULL END AS fi_ok,

    CASE WHEN se10 IS NOT NULL THEN
      CASE
        WHEN MOD(
          ( SELECT SUM(
              CASE
                WHEN MOD(level, 2) = 1 THEN
                  CASE
                    WHEN 2*TO_NUMBER(SUBSTR(se10, level, 1)) < 10
                      THEN 2*TO_NUMBER(SUBSTR(se10, level, 1))
                    ELSE 1 + (2*TO_NUMBER(SUBSTR(se10, level, 1)) - 10)
                  END
                ELSE
                  TO_NUMBER(SUBSTR(se10, level, 1))
              END)
            FROM dual CONNECT BY level <= 10 ), 10) = 0
        THEN 'Y' ELSE 'N' END
    ELSE NULL END AS se_ok,

    CASE WHEN fr_all IS NOT NULL THEN
      CASE
        WHEN TO_NUMBER(SUBSTR(normalized,3,2)) =
             MOD(12 + 3 * MOD(TO_NUMBER(SUBSTR(normalized,5,9)), 97), 97)
        THEN 'Y' ELSE 'N' END
    ELSE NULL END AS fr_ok,

    CASE WHEN nl9 IS NOT NULL THEN
      CASE
        WHEN MOD( 9*TO_NUMBER(SUBSTR(nl9,1,1)) +
                  8*TO_NUMBER(SUBSTR(nl9,2,1)) +
                  7*TO_NUMBER(SUBSTR(nl9,3,1)) +
                  6*TO_NUMBER(SUBSTR(nl9,4,1)) +
                  5*TO_NUMBER(SUBSTR(nl9,5,1)) +
                  4*TO_NUMBER(SUBSTR(nl9,6,1)) +
                  3*TO_NUMBER(SUBSTR(nl9,7,1)) +
                  2*TO_NUMBER(SUBSTR(nl9,8,1)) - 1*TO_NUMBER(SUBSTR(nl9,9,1)), 11) = 0
        THEN 'Y' ELSE 'N' END
    ELSE NULL END AS nl_ok,

    CASE WHEN be10 IS NOT NULL THEN
      CASE
        WHEN TO_NUMBER(SUBSTR(be10,9,2)) =
               MOD(97 - MOD(TO_NUMBER(SUBSTR(be10,1,8)),97), 97)
          OR TO_NUMBER(SUBSTR(be10,9,2)) =
               MOD(97 - MOD(TO_NUMBER(SUBSTR(be10,1,9)),97), 97)
        THEN 'Y' ELSE 'N' END
    ELSE NULL END AS be_ok
  FROM core c
),
status AS (
  SELECT
    party_id, party_name, party_type, iso2 AS country_code,
    raw_association_no, cleaned, normalized, uk9, pl10, de9, sk10, it11, dk8, fi8, se10, fr_all, nl9, be10,
    CASE
      WHEN iso2 = 'PL' THEN CASE WHEN pl10 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN pl_ok = 'Y' THEN 'VAT_PL_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 IN ('GB','XI') THEN CASE WHEN uk9 IS NULL THEN 'NOT_VAT_LIKE'
                                         WHEN gb_ok = 'Y' THEN 'VAT_GB_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'DE' THEN CASE WHEN de9 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN de_ok = 'Y' THEN 'VAT_DE_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'SK' THEN CASE WHEN sk10 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN sk_ok = 'Y' THEN 'VAT_SK_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'IT' THEN CASE WHEN it11 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN it_ok = 'Y' THEN 'VAT_IT_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'DK' THEN CASE WHEN dk8 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN dk_ok = 'Y' THEN 'VAT_DK_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'FI' THEN CASE WHEN fi8 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN fi_ok = 'Y' THEN 'VAT_FI_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'SE' THEN CASE WHEN se10 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN se_ok = 'Y' THEN 'VAT_SE_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'FR' THEN CASE WHEN fr_all IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN fr_ok = 'Y' THEN 'VAT_FR_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'NL' THEN CASE WHEN nl9 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN nl_ok = 'Y' THEN 'VAT_NL_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      WHEN iso2 = 'BE' THEN CASE WHEN be10 IS NULL THEN 'NOT_VAT_LIKE'
                                 WHEN be_ok = 'Y' THEN 'VAT_BE_OK' ELSE 'VAT_CHECKSUM_FAIL' END
      ELSE
        CASE WHEN REGEXP_LIKE(normalized, '^[A-Z]{2}[A-Z0-9]{2,}$') THEN 'FORMAT_ONLY'
             ELSE 'NOT_VAT_LIKE' END
    END AS validation_status,
    CASE
      WHEN iso2 IN ('PL','GB','XI','DE','SK','IT','DK','FI','SE','FR','NL','BE') AND
           ( (iso2 IN ('GB','XI') AND uk9 IS NULL) OR
             (iso2 = 'PL' AND pl10 IS NULL) OR
             (iso2 = 'DE' AND de9 IS NULL) OR
             (iso2 = 'SK' AND sk10 IS NULL) OR
             (iso2 = 'IT' AND it11 IS NULL) OR
             (iso2 = 'DK' AND dk8 IS NULL) OR
             (iso2 = 'FI' AND fi8 IS NULL) OR
             (iso2 = 'SE' AND se10 IS NULL) OR
             (iso2 = 'FR' AND fr_all IS NULL) OR
             (iso2 = 'NL' AND nl9 IS NULL) OR
             (iso2 = 'BE' AND be10 IS NULL) )
        THEN 'Missing expected format for country'
      WHEN iso2 = 'PL' AND pl10 IS NOT NULL AND pl_ok = 'N' THEN 'PL NIP: checksum fail'
      WHEN iso2 IN ('GB','XI') AND uk9 IS NOT NULL AND gb_ok = 'N' THEN 'UK VAT: checksum fail'
      WHEN iso2 = 'DE' AND de9 IS NOT NULL AND de_ok = 'N' THEN 'DE USt-IdNr: checksum fail'
      WHEN iso2 = 'SK' AND sk10 IS NOT NULL AND sk_ok = 'N' THEN 'SK VAT: not divisible by 11'
      WHEN iso2 = 'IT' AND it11 IS NOT NULL AND it_ok = 'N' THEN 'IT VAT: checksum fail'
      WHEN iso2 = 'DK' AND dk8 IS NOT NULL AND dk_ok = 'N' THEN 'DK VAT: checksum fail'
      WHEN iso2 = 'FI' AND fi8 IS NOT NULL AND fi_ok = 'N' THEN 'FI VAT: checksum fail'
      WHEN iso2 = 'SE' AND se10 IS NOT NULL AND se_ok = 'N' THEN 'SE VAT: Luhn fail'
      WHEN iso2 = 'FR' AND fr_all IS NOT NULL AND fr_ok = 'N' THEN 'FR VAT: key mismatch'
      WHEN iso2 = 'NL' AND nl9 IS NOT NULL AND nl_ok = 'N' THEN 'NL VAT: 11-proef fail'
      WHEN iso2 = 'BE' AND be10 IS NOT NULL AND be_ok = 'N' THEN 'BE VAT: checksum fail'
      WHEN REGEXP_LIKE(normalized, '^[A-Z]{2}[A-Z0-9]{2,}$') THEN 'Format only (no checksum rules for this country)'
      ELSE NULL
    END AS validation_detail
  FROM calc c
),
final AS (
  SELECT
    s.*,
    CASE
      WHEN s.validation_status IN ('VAT_PL_OK','VAT_GB_OK','VAT_DE_OK','VAT_SK_OK',
                                   'VAT_IT_OK','VAT_DK_OK','VAT_FI_OK','VAT_SE_OK',
                                   'VAT_FR_OK','VAT_NL_OK','VAT_BE_OK')
        THEN 'N' ELSE 'Y'
    END AS needs_attention
  FROM status s
)
SELECT party_id,party_name,party_type,country_code,raw_association_no,validation_status,validation_detail
FROM final
WHERE needs_attention = 'Y'
  AND country_code IN ('PL','GB','XI','DE','SK','IT','DK','FI','SE','FR','NL','BE')
  AND SUBSTR(party_id,1,1) <> 'P';
