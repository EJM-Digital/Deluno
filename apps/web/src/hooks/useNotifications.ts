import { useCallback, useEffect, useState } from "react";

export interface Notification {
  id: string;
  type: string;
  title: string;
  message: string;
  severity: "info" | "success" | "warning" | "error";
  createdUtc: string;
  readUtc?: string;
  metadata?: Record<string, any>;
}

export interface NotificationPreferences {
  searchCompletionEnabled: boolean;
  downloadStartedEnabled: boolean;
  downloadProgressEnabled: boolean;
  downloadCompletedEnabled: boolean;
  importStartedEnabled: boolean;
  importCompletedEnabled: boolean;
  importFailedEnabled: boolean;
  automationErrorEnabled: boolean;
  systemWarningsEnabled: boolean;
  inAppNotificationsEnabled: boolean;
  emailNotificationsEnabled: boolean;
  webhookNotificationsEnabled: boolean;
  emailAddress?: string;
  webhookUrl?: string;
}

const API_BASE = "http://127.0.0.1:5099/api";

export function useNotifications() {
  const [notifications, setNotifications] = useState<Notification[]>([]);
  const [unreadCount, setUnreadCount] = useState(0);
  const [preferences, setPreferences] = useState<NotificationPreferences | null>(
    null
  );
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Fetch all notifications
  const fetchNotifications = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await fetch(`${API_BASE}/notifications`, {
        headers: {
          Authorization: `Bearer ${localStorage.getItem("token")}`,
        },
      });
      if (!response.ok) throw new Error("Failed to fetch notifications");
      const data = await response.json();
      setNotifications(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setLoading(false);
    }
  }, []);

  // Get unread count
  const fetchUnreadCount = useCallback(async () => {
    try {
      const response = await fetch(`${API_BASE}/notifications/unread-count`, {
        headers: {
          Authorization: `Bearer ${localStorage.getItem("token")}`,
        },
      });
      if (!response.ok) throw new Error("Failed to fetch unread count");
      const data = await response.json();
      setUnreadCount(data.unreadCount);
    } catch (err) {
      console.error("Failed to fetch unread count:", err);
    }
  }, []);

  // Fetch preferences
  const fetchPreferences = useCallback(async () => {
    try {
      const response = await fetch(`${API_BASE}/notification-preferences`, {
        headers: {
          Authorization: `Bearer ${localStorage.getItem("token")}`,
        },
      });
      if (!response.ok) throw new Error("Failed to fetch preferences");
      const data = await response.json();
      setPreferences(data);
    } catch (err) {
      console.error("Failed to fetch preferences:", err);
    }
  }, []);

  // Mark notification as read
  const markAsRead = useCallback(
    async (notificationId: string) => {
      try {
        const response = await fetch(
          `${API_BASE}/notifications/${notificationId}/read`,
          {
            method: "POST",
            headers: {
              Authorization: `Bearer ${localStorage.getItem("token")}`,
            },
          }
        );
        if (!response.ok) throw new Error("Failed to mark as read");
        await fetchNotifications();
        await fetchUnreadCount();
      } catch (err) {
        console.error("Failed to mark as read:", err);
      }
    },
    [fetchNotifications, fetchUnreadCount]
  );

  // Mark all notifications as read
  const markAllAsRead = useCallback(async () => {
    try {
      const response = await fetch(`${API_BASE}/notifications/read-all`, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${localStorage.getItem("token")}`,
        },
      });
      if (!response.ok) throw new Error("Failed to mark all as read");
      await fetchNotifications();
      await fetchUnreadCount();
    } catch (err) {
      console.error("Failed to mark all as read:", err);
    }
  }, [fetchNotifications, fetchUnreadCount]);

  // Delete notification
  const deleteNotification = useCallback(
    async (notificationId: string) => {
      try {
        const response = await fetch(
          `${API_BASE}/notifications/${notificationId}`,
          {
            method: "DELETE",
            headers: {
              Authorization: `Bearer ${localStorage.getItem("token")}`,
            },
          }
        );
        if (!response.ok) throw new Error("Failed to delete notification");
        await fetchNotifications();
        await fetchUnreadCount();
      } catch (err) {
        console.error("Failed to delete notification:", err);
      }
    },
    [fetchNotifications, fetchUnreadCount]
  );

  // Clear all notifications
  const clearAll = useCallback(async () => {
    try {
      const response = await fetch(`${API_BASE}/notifications`, {
        method: "DELETE",
        headers: {
          Authorization: `Bearer ${localStorage.getItem("token")}`,
        },
      });
      if (!response.ok) throw new Error("Failed to clear notifications");
      await fetchNotifications();
      await fetchUnreadCount();
    } catch (err) {
      console.error("Failed to clear notifications:", err);
    }
  }, [fetchNotifications, fetchUnreadCount]);

  // Update preferences
  const updatePreferences = useCallback(
    async (newPreferences: NotificationPreferences) => {
      try {
        const response = await fetch(`${API_BASE}/notification-preferences`, {
          method: "PUT",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${localStorage.getItem("token")}`,
          },
          body: JSON.stringify(newPreferences),
        });
        if (!response.ok) throw new Error("Failed to update preferences");
        await fetchPreferences();
      } catch (err) {
        console.error("Failed to update preferences:", err);
      }
    },
    [fetchPreferences]
  );

  // Initial load
  useEffect(() => {
    fetchNotifications();
    fetchUnreadCount();
    fetchPreferences();

    // Poll for new notifications every 10 seconds
    const interval = setInterval(() => {
      fetchUnreadCount();
    }, 10000);

    return () => clearInterval(interval);
  }, [fetchNotifications, fetchUnreadCount, fetchPreferences]);

  return {
    notifications,
    unreadCount,
    preferences,
    loading,
    error,
    fetchNotifications,
    fetchUnreadCount,
    fetchPreferences,
    markAsRead,
    markAllAsRead,
    deleteNotification,
    clearAll,
    updatePreferences,
  };
}
