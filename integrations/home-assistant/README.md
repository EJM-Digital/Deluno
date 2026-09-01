# Deluno for Home Assistant

This is a configuration-only Home Assistant integration. It uses Deluno's
authenticated `/api/v1` API and never reads Deluno's database or scrapes its
web UI.

## Install

1. Create a Deluno API key from System → API access using the
   `home-assistant` template (`read,write,queue`). The key does not need
   `system` or `imports`.
2. Add these values to `secrets.yaml`:

   ```yaml
   deluno_api_key: deluno_replace_me
   deluno_notification_webhook_id: deluno_notifications
   ```

3. Copy `deluno.yaml` into a Home Assistant package directory and enable
   packages, or merge its five sections (`input_text`, `sensor`, `template`,
   `rest_command`, and `automation`) into `configuration.yaml`.
4. Set the `input_text.deluno_api_url` helper to the Deluno base URL, then
   replace `deluno_library_id` in the command examples with library IDs
   returned by `GET /api/v1/libraries`.
5. In Deluno, create an outbound notification webhook pointing to
   `https://<home-assistant-host>/api/webhook/<id>` and choose the event
   filters you want. The included automation turns those events into a
   persistent Home Assistant notification.
6. Restart Home Assistant and check Settings → Devices & services → Helpers,
   or reload the REST and template entities if your installation supports it.

The REST sensor polls the read-only summary once per minute. Its attributes
contain the full `readiness`, `queue`, `imports`, and `attention` objects, while
the template sensors expose the useful counters individually.

## Safe actions

The commands only call Deluno's existing scoped actions:

- `deluno_search_library` → run one library search;
- `deluno_pause_automation` / `deluno_resume_automation` → pause or resume
  Deluno's global background automation;
- `deluno_pause_existing_import` / `deluno_resume_existing_import` → pause or
  resume a tracked existing-library import;
- `deluno_approve_intake_preview` → approve an explicit, already-reviewed
  intake preview selection.

They do not delete media, change API keys, restore backups, apply updates, or
write to the Deluno database. Use the `home-assistant` key only for these
commands and the summary sensor.

For a visual notification automation, use the supplied
`blueprints/automation/deluno_notifications.yaml` instead of the included
automation block. The webhook payload is the same JSON emitted by Deluno.
