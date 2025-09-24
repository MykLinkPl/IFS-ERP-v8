BEGIN
  FOR r IN (SELECT contract,
                   catalog_no,
                   catalog_desc,
                   objid,
                   objversion,
                   REPLACE(REPLACE(REPLACE(catalog_desc, CHR(9), '⟦TAB⟧'),
                                   CHR(10),
                                   '⟦LF⟧'),
                           CHR(13),
                           '⟦CR⟧') AS ctrl_marked,
                   REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REPLACE(REPLACE(REPLACE(catalog_desc,
                                                                                        CHR(9),
                                                                                        ' '),
                                                                                CHR(10),
                                                                                ' '),
                                                                        CHR(13),
                                                                        ' '),
                                                                '[' ||
                                                                UNISTR('\00A0\2007\202F') || ']',
                                                                '⟦NBSP⟧'),
                                                 '[' ||
                                                 UNISTR('\200B\200C\200D\FEFF') || ']',
                                                 '⟦ZW⟧'),
                                  '(^[[:space:]]+)|([[:space:]]+$)',
                                  '⟦TRIM⟧') AS marked,
                   TRIM(REGEXP_REPLACE(REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REPLACE(REPLACE(REPLACE(catalog_desc,
                                                                                                     CHR(9),
                                                                                                     ' '),
                                                                                             CHR(10),
                                                                                             ' '),
                                                                                     CHR(13),
                                                                                     ' '),
                                                                             '[' ||
                                                                             UNISTR('\00A0\2007\202F') || ']',
                                                                             ' '),
                                                              '[' ||
                                                              UNISTR('\200B\200C\200D\FEFF') || ']',
                                                              ''),
                                               '"',
                                               ''),
                                       ' {2,}',
                                       ' ')) AS catalog_desc_clean
              FROM ifsapp.sales_part a
             WHERE REGEXP_LIKE(a.catalog_desc, '(^[[:space:]]|[[:space:]]$)')
                OR REGEXP_LIKE(REPLACE(a.catalog_desc, ' '), '[[:space:]]')
                OR INSTR(a.catalog_desc, UNISTR('\00A0')) > 0
                OR INSTR(a.catalog_desc, UNISTR('\2007')) > 0
                OR INSTR(a.catalog_desc, UNISTR('\202F')) > 0
                OR INSTR(a.catalog_desc, UNISTR('\200B')) > 0
                OR INSTR(a.catalog_desc, UNISTR('\200C')) > 0
                OR INSTR(a.catalog_desc, UNISTR('\200D')) > 0
                OR INSTR(a.catalog_desc, UNISTR('\FEFF')) > 0) LOOP
                    DECLARE
                      a_ VARCHAR2(32000) := ''; --p0
                      b_ VARCHAR2(32000) := r.objid; --p1
                      c_ VARCHAR2(32000) := r.objversion; --p2
                      d_ VARCHAR2(32000) := 'CATALOG_DESC' || chr(31) ||
                                            r.catalog_desc_clean ||
                                            chr(30); --p3
                      e_ VARCHAR2(32000) := 'DO'; --p4
                    BEGIN
                    
                      IFSAPP.Language_SYS.Set_Language('pl'); -- This requires logged in user to have privileges to Language_SYS.Set_Language
                    
                      IFSAPP.SALES_PART_API.MODIFY__(a_, b_, c_, d_, e_);
                    
                    END;
  
  END LOOP;
END;
/
