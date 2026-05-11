import React, { useState } from "react";
import { useNotifications, type Notification } from "../hooks/useNotifications";
import "./NotificationCenter.css";

export function NotificationCenter() {
  const [isOpen, setIsOpen] = useState(false);
  const {
    notifications,
    unreadCount,
    markAsRead,
    markAllAsRead,
    deleteNotification,
    clearAll,
  } = useNotifications();

  const getSeverityIcon = (severity: string): string => {
    switch (severity) {
      case "success":
        return "✓";
      case "error":
        return "✕";
      case "warning":
        return "⚠";
      case "info":
      default:
        return "ℹ";
    }
  };

  const getSeverityClass = (severity: string): string => {
    return `notification-${severity}`;
  };

  const formatTime = (dateString: string): string => {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return "just now";
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffHours < 24) return `${diffHours}h ago`;
    if (diffDays < 7) return `${diffDays}d ago`;

    return date.toLocaleDateString();
  };

  return (
    <div className="notification-center">
      {/* Bell icon with unread badge */}
      <div className="notification-bell" onClick={() => setIsOpen(!isOpen)}>
        <button
          className="bell-button"
          aria-label="Notifications"
          title="Notifications"
        >
          🔔
        </button>
        {unreadCount > 0 && (
          <span className="unread-badge">{unreadCount > 9 ? "9+" : unreadCount}</span>
        )}
      </div>

      {/* Notification panel */}
      {isOpen && (
        <div className="notification-panel">
          <div className="notification-header">
            <h3>Notifications</h3>
            <div className="notification-actions">
              {unreadCount > 0 && (
                <button
                  className="action-button"
                  onClick={() => markAllAsRead()}
                  title="Mark all as read"
                >
                  Mark all read
                </button>
              )}
              {notifications.length > 0 && (
                <button
                  className="action-button danger"
                  onClick={() => clearAll()}
                  title="Clear all notifications"
                >
                  Clear
                </button>
              )}
            </div>
          </div>

          <div className="notification-list">
            {notifications.length === 0 ? (
              <div className="empty-state">
                <p>No notifications</p>
              </div>
            ) : (
              notifications.map((notification: Notification) => (
                <div
                  key={notification.id}
                  className={`notification-item ${getSeverityClass(
                    notification.severity
                  )} ${notification.readUtc ? "read" : "unread"}`}
                  onClick={() => !notification.readUtc && markAsRead(notification.id)}
                >
                  <div className="notification-icon">
                    {getSeverityIcon(notification.severity)}
                  </div>
                  <div className="notification-content">
                    <div className="notification-title">{notification.title}</div>
                    <div className="notification-message">
                      {notification.message}
                    </div>
                    <div className="notification-time">
                      {formatTime(notification.createdUtc)}
                    </div>
                  </div>
                  <button
                    className="close-button"
                    onClick={(e) => {
                      e.stopPropagation();
                      deleteNotification(notification.id);
                    }}
                    title="Dismiss"
                    aria-label="Dismiss notification"
                  >
                    ✕
                  </button>
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}
