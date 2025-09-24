-- Copy PL Tax Declaration definitions between companies (PIT-4R example)
-- LU Name: PlTaxConfigType
-- Author: Przemysław Myk — MykLink / Smart Connections
-- https://github.com/MykLinkPl


DECLARE
  -- Parameters (adjust as needed)
  p_config_type_id   VARCHAR2(50)  := 'PIT-4R';
  p_config_id        VARCHAR2(100) := 'PIT-4R V11'; -- template
  p_target_company   VARCHAR2(10)  := 'COMP';  -- destination company (where ID will be read)
  p_lang             VARCHAR2(5)   := 'en';  -- session language for IFS APIs

  -- Cursor: take rows for given config, all companies except the target company
  CURSOR c_src IS
    SELECT *
      FROM ifsapp.PL_TAX_CONFIG_DET
     WHERE config_type_id = p_config_type_id
       AND config_id      = p_config_id
       AND company_id    <> p_target_company;

  -- Counter
  v_rows INTEGER := 1;
BEGIN
  FOR r IN c_src LOOP
    v_rows := v_rows + 1;

    -- Keep the exact parameter semantics from original code:
    -- a_ = p0 (unused / empty)
    -- b_ = p1 (OBJID)
    -- c_ = p2 (OBJVERSION)
    -- d_ = p3 (payload string: 'ID' || chr(31) || <target-id> || chr(30))
    -- e_ = p4 (action code, 'DO')
    DECLARE
      a_  VARCHAR2(32000) := '';               -- p0
      b_  VARCHAR2(32000) := r.objid;          -- p1
      c_  VARCHAR2(32000) := r.objversion;     -- p2
      d_  VARCHAR2(32000);                     -- p3 (filled after fetching target ID)
      e_  VARCHAR2(32000) := 'DO';             -- p4
      v_target_id VARCHAR2(255);
    BEGIN
      -- Find the ID in the target company for the same sequence number
      SELECT id
        INTO v_target_id
        FROM ifsapp.PL_TAX_CONFIG_DET
       WHERE config_type_id = p_config_type_id
         AND config_id      = p_config_id
         AND seq_no         = r.seq_no
         AND company_id     = p_target_company;

      -- Build payload for MODIFY__ (field 'ID' with the target company value)
      d_ := 'ID' || chr(31) || v_target_id || chr(30);

      -- Ensure session language
      IFSAPP.Language_SYS.Set_Language(p_lang);

      -- Apply change
      IFSAPP.PL_TAX_CONFIG_DET_API.MODIFY__(a_, b_, c_, d_, e_);
    END;
  END LOOP;

  DBMS_OUTPUT.PUT_LINE('Processed rows: ' || v_rows);
END;
