# IFS ERP v8 — Admin / Utility Scripts

A collection of small scripts for IFS Applications 8.  
Author: **Przemysław Myk — MykLink / Smart Connections**

---

## 📜 Script List

| File | Description | Parameters | Side Effects | Notes |
|------|-------------|------------|--------------|-------|
| `Reorder_ifs_erp_enumeration` | Re-sequences enumeration values (order by `CLIENT_VALUE`). | `v_lu` – LU name of the enumeration. | Commit after each row + `Deploy__` call. | If the enumeration was removed by another user, deploy is skipped. |

---

## 🧭 How to Run

1. Open the script in SQL*Plus / SQLcl / SQL Developer.
2. Adjust parameters at the top of the file (e.g., `v_lu`).
3. Run `SET SERVEROUTPUT ON` to see log messages.
4. Execute the script.
5. Refresh the Custom Fields window in IFS client to see the changes.

---

## ⚠️ Disclaimer

Always test scripts in a non-production environment first. Some scripts require access to the `IFSAPP` schema.
