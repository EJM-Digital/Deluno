import React, { useState, useEffect } from "react";
import {
  useNotifications,
  type NotificationPreferences,
} from "../hooks/useNotifications";

export function NotificationPreferencesPanel() {
  const { preferences, fetchPreferences, updatePreferences } =
    useNotifications();
  const [formData, setFormData] = useState<NotificationPreferences | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (preferences) {
      setFormData(preferences);
    }
  }, [preferences]);

  useEffect(() => {
    fetchPreferences();
  }, [fetchPreferences]);

  const handleToggle = (field: keyof NotificationPreferences) => {
    if (formData && typeof formData[field] === "boolean") {
      setFormData({
        ...formData,
        [field]: !formData[field],
      });
      setSaved(false);
    }
  };

  const handleChange = (
    field: keyof NotificationPreferences,
    value: string
  ) => {
    if (formData) {
      setFormData({
        ...formData,
        [field]: value,
      });
      setSaved(false);
    }
  };

  const handleSave = async () => {
    if (formData) {
      await updatePreferences(formData);
      setSaved(true);
      setTimeout(() => setSaved(false), 3000);
    }
  };

  if (!formData) {
    return <div>Loading notification preferences...</div>;
  }

  return (
    <div className="notification-preferences-panel">
      <h2>Notification Preferences</h2>

      <section className="preference-section">
        <h3>Notification Types</h3>
        <p className="section-description">
          Choose which types of notifications you want to receive.
        </p>

        <div className="preference-group">
          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.searchCompletionEnabled}
              onChange={() => handleToggle("searchCompletionEnabled")}
            />
            <span className="label-text">
              <strong>Search Completion</strong>
              <br />
              <small>Get notified when searches complete</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.downloadStartedEnabled}
              onChange={() => handleToggle("downloadStartedEnabled")}
            />
            <span className="label-text">
              <strong>Download Started</strong>
              <br />
              <small>Get notified when downloads begin</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.downloadProgressEnabled}
              onChange={() => handleToggle("downloadProgressEnabled")}
            />
            <span className="label-text">
              <strong>Download Progress</strong>
              <br />
              <small>Get periodic progress updates (every 25%)</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.downloadCompletedEnabled}
              onChange={() => handleToggle("downloadCompletedEnabled")}
            />
            <span className="label-text">
              <strong>Download Completed</strong>
              <br />
              <small>Get notified when downloads complete successfully</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.importStartedEnabled}
              onChange={() => handleToggle("importStartedEnabled")}
            />
            <span className="label-text">
              <strong>Import Started</strong>
              <br />
              <small>Get notified when imports begin</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.importCompletedEnabled}
              onChange={() => handleToggle("importCompletedEnabled")}
            />
            <span className="label-text">
              <strong>Import Completed</strong>
              <br />
              <small>Get notified when imports complete successfully</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.importFailedEnabled}
              onChange={() => handleToggle("importFailedEnabled")}
            />
            <span className="label-text">
              <strong>Import Failed</strong>
              <br />
              <small>Get alerted when imports fail</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.automationErrorEnabled}
              onChange={() => handleToggle("automationErrorEnabled")}
            />
            <span className="label-text">
              <strong>Automation Errors</strong>
              <br />
              <small>Get alerted when automations encounter errors</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.systemWarningsEnabled}
              onChange={() => handleToggle("systemWarningsEnabled")}
            />
            <span className="label-text">
              <strong>System Warnings</strong>
              <br />
              <small>Get alerted about system warnings</small>
            </span>
          </label>
        </div>
      </section>

      <section className="preference-section">
        <h3>Delivery Methods</h3>
        <p className="section-description">
          Choose how you want to receive notifications.
        </p>

        <div className="preference-group">
          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.inAppNotificationsEnabled}
              onChange={() => handleToggle("inAppNotificationsEnabled")}
            />
            <span className="label-text">
              <strong>In-App Notifications</strong>
              <br />
              <small>Notifications in Deluno interface</small>
            </span>
          </label>

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.emailNotificationsEnabled}
              onChange={() => handleToggle("emailNotificationsEnabled")}
            />
            <span className="label-text">
              <strong>Email Notifications</strong>
              <br />
              <small>Receive notifications via email</small>
            </span>
          </label>

          {formData.emailNotificationsEnabled && (
            <div className="preference-input">
              <label htmlFor="emailAddress">Email Address</label>
              <input
                id="emailAddress"
                type="email"
                value={formData.emailAddress || ""}
                onChange={(e) => handleChange("emailAddress", e.target.value)}
                placeholder="your@email.com"
              />
            </div>
          )}

          <label className="preference-item">
            <input
              type="checkbox"
              checked={formData.webhookNotificationsEnabled}
              onChange={() => handleToggle("webhookNotificationsEnabled")}
            />
            <span className="label-text">
              <strong>Webhook Notifications</strong>
              <br />
              <small>Send notifications to a webhook endpoint</small>
            </span>
          </label>

          {formData.webhookNotificationsEnabled && (
            <div className="preference-input">
              <label htmlFor="webhookUrl">Webhook URL</label>
              <input
                id="webhookUrl"
                type="url"
                value={formData.webhookUrl || ""}
                onChange={(e) => handleChange("webhookUrl", e.target.value)}
                placeholder="https://webhook.example.com/notifications"
              />
            </div>
          )}
        </div>
      </section>

      <div className="preference-actions">
        <button onClick={handleSave} className="save-button">
          Save Preferences
        </button>
        {saved && <span className="saved-message">✓ Saved successfully</span>}
      </div>
    </div>
  );
}
