/*
Script resreshes order in IFS enumerations - IFS8 - 
Created by MykLink / Smart Connections
https://github.com/MykLinkPl
*/
DECLARE
  -- Parameters
  v_lu   VARCHAR2(100) := 'ENUM_LU'; -- TODO: put correct IFS enumeration LU
  v_lang varchar2(2) :='en'; -- put language
  -- Cursor: ordered by CLIENT_VALUE
  CURSOR c_enum IS
    SELECT rowkey, client_value, seq_no
    FROM   ifsapp.CUSTOM_FIELD_ENUM_VALUES_TAB
    WHERE  lu = v_lu
    ORDER  BY client_value ASC;

  -- Counters/vars
  i   PLS_INTEGER := 1;   -- new sequence value
  b_  DATE        := NULL;
BEGIN
  -- Ensure session language (UI-related APIs)
  IFSAPP.Language_SYS.Set_Language(v_lang);

  -- Re-sequence values in the chosen order
  FOR r IN c_enum LOOP
    DBMS_OUTPUT.PUT_LINE(r.client_value || ' - seq ' || r.seq_no || ' -> ' || i);

    UPDATE ifsapp.CUSTOM_FIELD_ENUM_VALUES_TAB
       SET seq_no = i
     WHERE lu          = v_lu
       AND client_value = r.client_value
       AND rowkey       = r.rowkey;

    COMMIT;          -- keep per-row commit (unchanged behavior)
    i := i + 1;
  END LOOP;

  -- Deploy enumeration so the UI picks up new order
  IFSAPP.Custom_Enumerations_API.Deploy__(v_lu);
  b_ := IFSAPP.Custom_Enumerations_API.Get_Published_Date(v_lu);
  COMMIT;

  DBMS_OUTPUT.PUT_LINE('Published date: ' || TO_CHAR(b_, 'YYYY-MM-DD HH24:MI:SS'));
END;
/
-- Refresh the Custom Fields window MANUALLY ( right mouse click -> Custom Objects -> Reload configuration -  to see the new order.
