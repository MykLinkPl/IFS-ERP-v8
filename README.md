# IFS ERP v8 — Admin / Utility Scripts

A collection of small scripts for IFS Applications 8.  
Author: **Przemysław Myk — MykLink / Smart Connections**

---

## 📜 Script List

| File | Description | Parameters | Side Effects | Notes |
|------|-------------|------------|--------------|-------|
| `Reorder_ifs_erp_enumeration` | Re-sequences enumeration values (order by `CLIENT_VALUE`). | `v_lu` – LU name of the enumeration. | Commit after each row + `Deploy__` call. | If the enumeration was removed by another user, deploy is skipped. |
| `Copy_tax_declaration_between` | Copies Tax Declaration definitions between companies (example for `PIT-4R` / `PIT-4R V11`). Uses `PL_TAX_CONFIG_DET_API.MODIFY__` to apply the target company’s `ID` (matched by `SEQ_NO`). | `p_config_type_id`, `p_config_id`, `p_target_company`, `p_lang` | Updates via `MODIFY__` (IFS API). | Table: `IFSAPP.PL_TAX_CONFIG_DET`, LU: `PlTaxConfigType`. Assumes the same `SEQ_NO` exists in the target company; adjust if additional fields need copying. Test on non-prod first. |
| `List_custom_artifacts_fields_enums_menus` | Bulk list of IFS custom artifacts (objects, custom fields/LUs, info cards, enumerations, menus, events, quick reports). Prefers `CUSTOM_OBJECTS_ALL`; falls back to unions when not available. | — | Read-only (prints semicolon-separated lines). | If `App_Config_Package_API` is present, includes package name for items (via `Get_Item_Package_Name`). |

---

## 🧭 How to Run

1. Open the script in SQL*Plus / SQLcl / SQL Developer.
2. Adjust parameters at the top of the file (e.g., `v_lu`).
3. Execute the script.
4. Read comments at the end of code to notice next steps.

---

## ⚠️ Disclaimer

Always test scripts in a non-production environment first. Some scripts require access to the `IFSAPP` schema.
