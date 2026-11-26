# User guide

## Everyone

* **Sign in** with your username **or** employee code and your password. After several wrong passwords, sign-in for that name pauses for a few minutes ("Too many attempts. Wait a minute and try again."); wait, or ask an administrator to reset your password.
* **First start on a device:** tap **Server settings**, enter the address your IT team gives you (it starts with `https://`), tap **Test connection**.
* **Session expired:** you are taken back to sign-in. Sign in again. If an administrator reset your password, changed your role or deactivated you, older sign-ins on every device end immediately.
* **Settings** shows the app version and lets you sign out.

## Quality inspector: recording a rejection

1. **Home** → tap your shop. You only see shops assigned to you.
2. **Stage** is skipped automatically when the shop has only one stage (true for all four shops today).
3. **Machine** → choose the machine (only active machines are listed).
4. **Rejection entry:** search and choose the phenomenon, set the quantity (1-9,999; type it or use + / −), and add remarks if needed (up to 500 characters). Tap **Save** once.
5. **Confirmation** shows what the server saved: shop, stage, machine, phenomenon, quantity, remarks and server time. Tap **Record another** for the next one.

**If something goes wrong**

| You see | What it means | What to do |
|---|---|---|
| "…may not have been saved. Tap Save again - it will not create a duplicate." | The network failed and the app does not know if the server got it | Tap **Save** again (the form is still filled). It can never create two entries. |
| "Already saved earlier – no duplicate created" on the confirmation | Your earlier attempt did reach the server | Nothing; it is saved once |
| "The selected rejection phenomenon is no longer active" | An administrator just deactivated it | The list reloads; choose again |
| "Machine no longer active" | An administrator just deactivated the machine | You are taken back to the refreshed machine list |
| Session expired while saving | Your sign-in ended during the save | Sign in again as the **same** user; the entry reopens with the same details. Tap **Save**; it cannot duplicate. |
| "An earlier attempt of this entry may already be saved with different details" | You changed the form after an uncertain save | Check **My entries** before saving again |

**Closing the app** with an unsaved entry discards it (the app works online only). If you had already tapped Save, check **My entries**.

**My entries** lists your own entries, newest first. Pull down to refresh; tap **Load more** at the bottom for older ones. If a box says some shops' entries "could not be loaded right now", the list is **incomplete**; try again later.

Entries cannot be edited or deleted in the app. Tell your supervisor about a mistake (correction rules are still to be decided by RJWL).

## Supervisor

* **Entries** shows all entries of your assigned shops (all inspectors), newest first, with **Load more** and the same "incomplete" notice.
* Supervisors do not record entries and cannot change master data.

## Administrator

Home shows three extra buttons: **Manage rejection phenomena**, **Manage machines**, **Manage users**.

**Rejection phenomena:** filter by shop, search, add, rename, activate/deactivate, delete.
* Names keep their exact spelling. A name that differs only in spaces or upper/lower case from an existing one in the same stage is refused.
* Codes (`GDC_SHOP-026` …) are assigned automatically.
* **Delete** works only for a phenomenon never used in an entry; otherwise **deactivate** it. Old entries keep the name they were saved with.

**Machines:** filter by shop, add (code unique within the shop), edit code and name, activate/deactivate, delete (only if never used). For a long list, IT can load it with `db/10_load_production_machines.sql`.

**Users:** add (employee code 2-30 characters, username 2-50, display name, role, shops, initial password of at least 10 characters), edit name/role/shops, activate/deactivate, **reset password**.
* A username can never equal another person's employee code, or the other way round (both are used to sign in).
* You cannot deactivate yourself or change your own role, and the last active administrator cannot be removed.
* Users are never deleted; deactivate them instead.
* Resetting a password, deactivating a user or changing a role signs that user out everywhere.

**"This record was changed by someone else after you opened it"**: another administrator saved the same record while you had it open. Go back, let the list reload, and make your change again.

Every administrative change is recorded in the audit log (who, what, when, before and after). Passwords are never recorded. There is no screen to browse the log; IT can query `qa.AuditLog`.
