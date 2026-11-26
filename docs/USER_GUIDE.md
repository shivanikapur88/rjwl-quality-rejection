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

**Fixing a mistake:** entries are never edited. Open **My entries**, tap the entry, then **Request correction**. Give a reason, and either choose **Remove this entry** or **Correct the details** (phenomenon, machine, quantity, remarks). Your supervisor approves or rejects it. Until then the entry stays as it is and shows "Correction requested". **My correction requests** on Home shows your requests; you can withdraw one while it is waiting. Entries older than 30 days cannot be corrected from the app.

**Scan machine QR** (Home): point the camera at the label on the machine and the entry form opens for it. Allow camera access the first time. If a label is damaged or not recognised, choose the machine from the list as before.

**Shift:** every entry belongs to a production day and shift (A 06:00–14:00, B 14:00–22:00, C 22:00–06:00). Night-shift entries after midnight count for the previous day. The confirmation screen and My entries show it.

## Supervisor

* **Entries** shows all entries of your assigned shops (all inspectors), newest first, with **Load more** and the same "incomplete" notice.
* Supervisors do not record entries and cannot change master data.
* **Correction requests:** approve (the entry is reversed, and the corrected entry is saved if one was proposed) or reject (give a reason; the inspector sees it). Tick "Also show decided requests" for history.
* **Shift summary:** rejected parts for today's production day and the current shift. Use ‹ › to change the day and the picker to change the shift. Shows totals by shop, top phenomena and top machines.
* **Web dashboard:** open `https://<server>/dashboard/` in a browser and sign in with your app username and password.
  * **Drill down:** click a day in the chart, a phenomenon in the Pareto, or a machine or shop row. Everything filters to it, and the entries behind it are listed at the bottom. Chips above the tiles show the active filters; click × to remove one. You can copy the address to share the same view.
  * **Comparison:** the tiles and the "vs prev." columns compare with the previous period of the same length (for example the 7 days before).
  * **Pareto:** phenomena from most to fewest rejected parts, with the cumulative share and a line where 80% of rejected parts is reached. These are the few causes worth attacking first.
  * **Rejection rate:** rejected ÷ produced parts. It appears only where production counts have been loaded, and the "based on N% of rejected parts" note says how much of the data it covers. It shows trends by day and shift, top phenomena and machines, and totals per shop for any period up to 92 days, with a table view of the chart.

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

**Production counts (for the rejection rate):** on the dashboard, in the "Production counts" box, choose a CSV file with the columns `shop,machine,date,shift,produced` ("Download a template" gives an example). Press **Check file**: nothing is saved yet, and any wrong rows are listed. Then press **Save counts**. Uploading the same machine, day and shift again replaces the earlier number.

**Machine QR labels:** in a browser, open `https://<server>/dashboard/labels.html`, choose the shop, then **Print**. Stick each label on its machine. Labels stay valid when a machine is renamed. Administrators can also approve or reject correction requests, and use the shift summary and dashboard for all shops.

Every administrative change is recorded in the audit log (who, what, when, before and after). Passwords are never recorded. There is no screen to browse the log; IT can query `qa.AuditLog`.
