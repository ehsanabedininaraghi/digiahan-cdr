# DigiAhan CDR v4.3.9

Release date: 2026-08-10

## Private management dashboard

- Removes every link from the SMS operator dashboard back to the management dashboard.
- Protects the management dashboard, dashboard reports, sales reports, and system-health APIs with a password login.
- Uses an HTTP-only, same-site session cookie and expires access when the browser session ends.
- Rate-limits failed dashboard login attempts.
- Prompts for a private dashboard password during first v4.3.9 installation and stores only its SHA-256 hash in a local-only configuration file.
- Keeps the SMS operator and agent dashboards independent and accessible on the trusted private LAN.

## Agent connectivity and LAN SMS hotfix

- Prevents overlapping agent-panel polls and reduces polling from 1.5 to 3 seconds.
- Marks the panel disconnected only after three consecutive complete polling failures.
- Accepts a successful response from either paired extension instead of flickering on one transient failure.
- Uses the private-LAN SMS operator endpoints for preparing and marking messages sent, eliminating the false HTTP 401 on the invoice-notification page.
- Adds the updated agent assets to the installer payload and forces a fresh browser cache version.

## SQL hotfix

- Fixes the internal-server error in the invoice and SMS dashboards.
- Uses the actual `InvoiceNotificationAttempts.AttemptId` key when reading the latest sender.
- The reported failure was a dashboard query error, not an accounting-server connection failure.

## Operator dashboard follow-up

- Restores the familiar invoice-notification table layout for the SMS operator.
- Shows every mobile number connected to the customer identity and highlights the primary number.
- Adds a same-day sent-history table with send time and operator name.
- Persists the operator name in the notification audit trail for accountability.

## SMS operator dashboard

- Adds a separate `/sms-dashboard` page for the employee who manually sends customer messages.
- The operator can prepare and copy the exact message, then record it as manually sent.
- The operator workflow is restricted to the private company LAN and does not expose management actions.
- Removes the unfinished public website link from prepared SMS messages.
- Personalizes every message with the customer name; the product line is omitted when product data is unavailable.
- Keeps the public-token infrastructure dormant for a future designed customer page.

## LAN list access hotfix

- Allows read-only invoice-notification listing from RFC1918 private LAN addresses without a management token.
- Keeps discovery, phone changes, message preparation, and sent-status writes protected by the management token.
- Forces fresh v4.3.9 UI assets to avoid stale browser cache.

## Hotfix

- Binds the Persian `@today` and `@yesterday` SQL parameters before executing invoice-notification discovery.
- Restores successful accounting job completion and population of the voucher list.
- Keeps the fast offline installer build path.

## Daily delivery voucher workflow

- Treats every non-empty accounting `factor.description` as the complete delivery-voucher reference.
- Displays and discovers notifications only for the current Persian day and the prior Persian day.
- Changes the accounting incremental window to two days.
- Marks a `READY` or `PREPARED` row as manually sent when its checkbox is confirmed, then removes it from the page.
- Keeps sent records as audit history while excluding them from the working list.
- Expands the stored voucher-description field to preserve longer accounting descriptions.
- Retains manual secure-link and SMS-text preparation.
