-- Bulk export of IFS “custom” artifacts (objects, custom fields/LUs, info cards, enums, menus, events, quick reports)
-- Output: semicolon-separated lines (one object per line)
-- Author: Przemysław Myk — MykLink / Smart Connections

DECLARE
  ------------------------------------------------------------------------------
  -- Variables (declare BEFORE subprograms in an anonymous block)
  ------------------------------------------------------------------------------
  v_stmt_objs         VARCHAR2(32767);
  v_stmt_objs_no_pkg  VARCHAR2(32767);
  v_stmt_fields_union VARCHAR2(32767);
  v_stmt_fallback     VARCHAR2(32767);

  v_line VARCHAR2(4000);

  TYPE refcur IS REF CURSOR;
  rc refcur;

  ------------------------------------------------------------------------------
  -- Helper subprograms
  ------------------------------------------------------------------------------
  /* Check if an object is visible to current session */
  FUNCTION has_object(p_object_name IN VARCHAR2) RETURN BOOLEAN IS
    v_dummy INTEGER;
  BEGIN
    SELECT 1
      INTO v_dummy
      FROM ALL_OBJECTS
     WHERE OBJECT_NAME = UPPER(p_object_name)
       AND ROWNUM = 1;
    RETURN TRUE;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RETURN FALSE;
  END has_object;
BEGIN
  /* Build the statements. Using q'[ ... ]' so single quotes inside need no doubling. */

  -- 1) Preferred: CUSTOM_OBJECTS_ALL (with package name if App_Config_Package_API exists)
  v_stmt_objs := q'[
    SELECT NAME || ';' ||
           TRANSLATE(TRANSLATE(info, CHR(10) || CHR(13) || CHR(9), ' '), ';', ',') || ';' ||
           application_object || ';' ||
           type || ';' ||
           title || ';' ||
           approved || ';' ||
           published || ';' ||
           po_id || ';' ||
           App_Config_Package_API.Get_Item_Package_Name(objkey)
      FROM custom_objects_all
  ]';

  -- 1a) Same without App_Config_Package_API
  v_stmt_objs_no_pkg := q'[
    SELECT NAME || ';' ||
           TRANSLATE(TRANSLATE(info, CHR(10) || CHR(13) || CHR(9), ' '), ';', ',') || ';' ||
           application_object || ';' ||
           type || ';' ||
           title || ';' ||
           approved || ';' ||
           published || ';' ||
           po_id || ';' ||
           NULL
      FROM custom_objects_all
  ]';

  -- 2) Union across custom fields/LUs/info cards/enums/menus/events/quick reports
  v_stmt_fields_union := q'[
    SELECT NAME || ';' ||
           TRANSLATE(TRANSLATE(info, CHR(10) || CHR(13) || CHR(9), ' '), ';', ',') || ';' ||
           application_object || ';' ||
           type_db || ';' ||
           title || ';' ||
           approved || ';' ||
           published || ';' ||
           po_id || ';' ||
           NULL
      FROM (
            SELECT cfa.lu              AS application_object,
                   'CUSTOM_FIELD'      AS type_db,
                   cfa.prompt          AS title,
                   cfa.attribute_name  AS name,
                   cfa.used_db         AS approved,
                   cfa.published_db    AS published,
                   cfa.note            AS info,
                   cf.po_id            AS po_id
              FROM custom_field_attributes cfa
              JOIN custom_fields_tab cf
                ON cf.lu = cfa.lu
               AND cf.lu_type = cfa.lu_type
             WHERE cfa.lu_type = 'CUSTOM_FIELD'
            UNION
            SELECT lu, 'CUSTOM_LU', NULL, package_name, used_db, published_db, NULL, NULL
              FROM custom_lus
            UNION
            SELECT cfa.lu, 'CUSTOM_LU_ATTRIBUTE', cfa.prompt, cfa.attribute_name,
                   cfa.used_db, cfa.published_db, cfa.note, NULL
              FROM custom_field_attributes cfa
              JOIN custom_lus l
                ON l.lu = cfa.lu
               AND l.lu_type = cfa.lu_type
             WHERE cfa.lu_type = 'CUSTOM_LU'
            UNION
            SELECT cfa.lu, 'INFORMATION_CARD', cfa.prompt, cfa.attribute_name,
                   cfa.used_db, cfa.published_db, cfa.note, ci.po_id
              FROM custom_field_attributes cfa
              JOIN custom_info_cards_tab ci
                ON ci.lu = cfa.lu
               AND ci.lu_type = cfa.lu_type
             WHERE cfa.lu_type = 'INFO_CARD'
            UNION
            SELECT lu, 'CUSTOM_ENUMERATION', NULL, lu, used_db, published_db, NULL, NULL
              FROM custom_enumerations
            UNION
            SELECT cm.window, 'CUSTOM_MENU', cmt.title, TO_CHAR(cm.menu_id),
                   'TRUE', 'TRUE', NULL, cm.po_id
              FROM custom_menu cm
              JOIN custom_menu_text cmt
                ON cmt.menu_id = cm.menu_id
             WHERE cmt.language_code = language_sys.get_language()
            UNION
            SELECT event_lu_name, 'CUSTOM_EVENT', NULL, event_id,
                   event_enable, event_enable, event_description, NULL
              FROM fnd_event
             WHERE event_type_db = 'CUSTOM'
            UNION
            SELECT fea.event_lu_name, 'CUSTOM_EVENT_ACTION', fea.fnd_event_action_type,
                   fea.event_id, fea.action_enable, fea.action_enable, fea.description, NULL
              FROM fnd_event_action fea
              JOIN fnd_event fe
                ON fe.event_id = fea.event_id
               AND fe.event_lu_name = fea.event_lu_name
             WHERE fe.event_type_db = 'CUSTOM'
            UNION
            SELECT category_description, 'QUICK_REPORT', description,
                   TO_CHAR(quick_report_id), 'TRUE', 'TRUE', comments, po_id
              FROM quick_report
           )
  ]';

  -- 3) Minimal fallback (menus/events/quick reports only)
  v_stmt_fallback := q'[
    SELECT NAME || ';' ||
           TRANSLATE(TRANSLATE(info, CHR(10) || CHR(13) || CHR(9), ' '), ';', ',') || ';' ||
           application_object || ';' ||
           type_db || ';' ||
           title || ';' ||
           approved || ';' ||
           published || ';' ||
           po_id || ';' ||
           NULL
      FROM (
            SELECT cm.window AS application_object,
                   'CUSTOM_MENU' AS type_db,
                   cmt.title AS title,
                   TO_CHAR(cm.menu_id) AS name,
                   'TRUE' AS approved,
                   'TRUE' AS published,
                   NULL AS info,
                   NULL AS po_id
              FROM custom_menu cm
              JOIN custom_menu_text cmt
                ON cmt.menu_id = cm.menu_id
             WHERE cmt.language_code = language_sys.get_language()
            UNION
            SELECT event_lu_name, 'CUSTOM_EVENT', NULL, event_id,
                   event_enable, event_enable, event_description, NULL
              FROM fnd_event
             WHERE event_type_db = 'CUSTOM'
            UNION
            SELECT fea.event_lu_name, 'CUSTOM_EVENT_ACTION', fea.fnd_event_action_type,
                   fea.event_id, fea.action_enable, fea.action_enable, fea.description, NULL
              FROM fnd_event_action fea
              JOIN fnd_event fe
                ON fe.event_id = fea.event_id
               AND fe.event_lu_name = fea.event_lu_name
             WHERE fe.event_type_db = 'CUSTOM'
            UNION
            SELECT category_description, 'QUICK_REPORT', description,
                   TO_CHAR(quick_report_id), 'TRUE', 'TRUE', comments, po_id
              FROM quick_report
           )
  ]';

  ------------------------------------------------------------------------------
  -- Choose best available source
  ------------------------------------------------------------------------------
  IF has_object('CUSTOM_OBJECTS_ALL') THEN
    IF has_object('APP_CONFIG_PACKAGE_API') THEN
      OPEN rc FOR v_stmt_objs;
    ELSE
      OPEN rc FOR v_stmt_objs_no_pkg;
    END IF;
  ELSIF has_object('CUSTOM_FIELDS_TAB') THEN
    OPEN rc FOR v_stmt_fields_union;
  ELSE
    OPEN rc FOR v_stmt_fallback;
  END IF;

  LOOP
    FETCH rc INTO v_line;
    EXIT WHEN rc%NOTFOUND;
    DBMS_OUTPUT.PUT_LINE(v_line);
  END LOOP;
  CLOSE rc;
END;
/
