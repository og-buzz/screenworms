/*
 * screenworm-idle: wait for compositor idle and run the screensaver.
 *
 * KDE Plasma on Wayland does not expose idle time over D-Bus. The stable
 * ext-idle-notify-v1 protocol does. This helper owns one idle notification,
 * starts the command when the seat goes idle, and terminates it on resume.
 */
#define _GNU_SOURCE
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <fcntl.h>
#include <wayland-client.h>

#ifdef HAVE_SYSTEMD
#include <systemd/sd-bus.h>
#endif

#include "ext-idle-notify-v1-client-protocol.h"

static struct wl_display *display;
static struct wl_registry *registry;
static struct wl_seat *seat;
static struct ext_idle_notifier_v1 *notifier;
static struct ext_idle_notification_v1 *notification;
static volatile sig_atomic_t running = 1;
static volatile sig_atomic_t child_pid = 0;
static int verbose;
static int wake_fds[2] = { -1, -1 };
static char **command;
static const char *default_command[] = { "screenworm", "--run", NULL };
#ifdef HAVE_SYSTEMD
static sd_bus *bus;
#endif

static int kde_screen_active(void)
{
#ifdef HAVE_SYSTEMD
    if (!bus) return 0;
    sd_bus_error error = SD_BUS_ERROR_NULL;
    sd_bus_message *reply = NULL;
    int active = 0;
    int r = sd_bus_call_method(bus, "org.kde.screensaver", "/ScreenSaver",
        "org.freedesktop.ScreenSaver", "GetActive", &error, &reply, "");
    if (r >= 0) sd_bus_message_read(reply, "b", &active);
    if (reply) sd_bus_message_unref(reply);
    sd_bus_error_free(&error);
    return active;
#else
    return 0;
#endif
}

static void logmsg(const char *msg)
{
    if (verbose) fprintf(stderr, "screenworm-idle: %s\n", msg);
    fflush(stderr);
}

static void stop_child(void)
{
    sigset_t block, old;
    sigemptyset(&block);
    sigaddset(&block, SIGCHLD);
    sigprocmask(SIG_BLOCK, &block, &old);

    pid_t pid = (pid_t)child_pid;
    child_pid = 0;
    if (pid > 0) {
        kill(-pid, SIGTERM);
        kill(pid, SIGTERM);
        for (int i = 0; i < 30; i++) {
            pid_t r = waitpid(pid, NULL, WNOHANG);
            if (r == pid) { pid = 0; break; }
            usleep(100000);
        }
        if (pid > 0) {
            kill(-pid, SIGKILL);
            kill(pid, SIGKILL);
            waitpid(pid, NULL, 0);
        }
    }

    sigprocmask(SIG_SETMASK, &old, NULL);
}

static void start_child(void)
{
    sigset_t block, old;
    sigemptyset(&block);
    sigaddset(&block, SIGCHLD);
    sigprocmask(SIG_BLOCK, &block, &old);

    if (child_pid > 0) {
        sigprocmask(SIG_SETMASK, &old, NULL);
        return;
    }
    pid_t pid = fork();
    if (pid < 0) {
        logmsg("fork failed");
        sigprocmask(SIG_SETMASK, &old, NULL);
        return;
    }
    if (pid == 0) {
        sigprocmask(SIG_SETMASK, &old, NULL);
        setsid();
        execvp(command[0], command);
        fprintf(stderr, "screenworm-idle: exec %s failed: %s\n", command[0], strerror(errno));
        _exit(127);
    }
    child_pid = pid;
    sigprocmask(SIG_SETMASK, &old, NULL);
    logmsg("idle, started screensaver");
}

static void on_idled(void *data, struct ext_idle_notification_v1 *n)
{
    (void)data; (void)n;
    start_child();
}

static void on_resumed(void *data, struct ext_idle_notification_v1 *n)
{
    (void)data; (void)n;
    logmsg("resumed, stopping screensaver");
    stop_child();
}

static const struct ext_idle_notification_v1_listener notification_listener = {
    .idled = on_idled,
    .resumed = on_resumed,
};

static void on_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t version)
{
    (void)data;
    if (strcmp(iface, ext_idle_notifier_v1_interface.name) == 0 && !notifier) {
        notifier = wl_registry_bind(reg, name, &ext_idle_notifier_v1_interface, 1);
        logmsg("bound ext_idle_notifier_v1");
    } else if (strcmp(iface, wl_seat_interface.name) == 0 && !seat) {
        uint32_t v = version < 7 ? version : 7;
        seat = wl_registry_bind(reg, name, &wl_seat_interface, v);
        logmsg("bound wl_seat");
    }
}

static void on_global_remove(void *data, struct wl_registry *reg, uint32_t name)
{
    (void)data; (void)reg; (void)name;
}

static const struct wl_registry_listener registry_listener = {
    .global = on_global,
    .global_remove = on_global_remove,
};

static void on_signal(int sig)
{
    (void)sig;
    running = 0;
    if (wake_fds[1] >= 0) {
        char b = 1;
        ssize_t n = write(wake_fds[1], &b, 1);
        (void)n;
    }
}

static void on_sigchld(int sig)
{
    (void)sig;
    if (child_pid > 0) {
        pid_t r = waitpid((pid_t)child_pid, NULL, WNOHANG);
        if (r == (pid_t)child_pid) child_pid = 0;
    }
}

static void usage(const char *argv0)
{
    fprintf(stderr,
        "Usage: %s [--timeout SECONDS] [--verbose] [--] command [args...]\n"
        "  --timeout N   idle time before starting (default 300)\n"
        "  command       default: screenworm --run\n", argv0);
}

int main(int argc, char **argv)
{
    int timeout = 300;
    int i = 1;
    for (; i < argc; i++) {
        if (strcmp(argv[i], "--timeout") == 0 && i + 1 < argc) timeout = atoi(argv[++i]);
        else if (strcmp(argv[i], "--verbose") == 0 || strcmp(argv[i], "-v") == 0) verbose = 1;
        else if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) { usage(argv[0]); return 0; }
        else if (strcmp(argv[i], "--") == 0) { i++; break; }
        else break;
    }
    command = (i < argc) ? &argv[i] : (char **)default_command;
    if (timeout < 1) timeout = 1;

    if (pipe(wake_fds) == 0) {
        fcntl(wake_fds[0], F_SETFL, O_NONBLOCK);
        fcntl(wake_fds[1], F_SETFL, O_NONBLOCK);
    }

    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = on_signal;
    sigaction(SIGTERM, &sa, NULL);
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGHUP, &sa, NULL);
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = on_sigchld;
    sa.sa_flags = SA_RESTART | SA_NOCLDSTOP;
    sigaction(SIGCHLD, &sa, NULL);

#ifdef HAVE_SYSTEMD
    if (sd_bus_open_user(&bus) < 0) {
        bus = NULL;
        logmsg("KDE lock detection unavailable (no session bus)");
    } else {
        sd_bus_set_method_call_timeout(bus, 200000);
        logmsg("KDE lock detection enabled");
    }
#endif

    display = wl_display_connect(NULL);
    if (!display) { fprintf(stderr, "screenworm-idle: cannot connect to Wayland display\n"); return 1; }
    registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, NULL);
    if (wl_display_roundtrip(display) < 0) { fprintf(stderr, "screenworm-idle: Wayland roundtrip failed\n"); return 1; }
    if (!notifier || !seat) {
        fprintf(stderr, "screenworm-idle: compositor does not expose ext_idle_notifier_v1 and wl_seat\n");
        return 1;
    }
    notification = ext_idle_notifier_v1_get_idle_notification(notifier, (uint32_t)timeout * 1000u, seat);
    ext_idle_notification_v1_add_listener(notification, &notification_listener, NULL);
    if (wl_display_roundtrip(display) < 0) { fprintf(stderr, "screenworm-idle: idle notification failed\n"); return 1; }
    logmsg("waiting for idle");

    while (running) {
        while (wl_display_prepare_read(display) != 0) {
            if (wl_display_dispatch_pending(display) < 0) { running = 0; break; }
        }
        if (!running) break;
        wl_display_flush(display);
        struct pollfd fds[2];
        fds[0].fd = wl_display_get_fd(display); fds[0].events = POLLIN; fds[0].revents = 0;
        fds[1].fd = wake_fds[0]; fds[1].events = POLLIN; fds[1].revents = 0;
        int n = poll(fds, 2, child_pid > 0 ? 1000 : -1);
        if (n < 0) {
            wl_display_cancel_read(display);
            if (errno == EINTR) continue;
            break;
        }
        if (fds[0].revents & POLLIN) {
            if (wl_display_read_events(display) < 0) { running = 0; break; }
            if (wl_display_dispatch_pending(display) < 0) { running = 0; break; }
        } else {
            wl_display_cancel_read(display);
        }
        if (fds[1].revents & POLLIN) {
            char buf[64];
            while (read(wake_fds[0], buf, sizeof(buf)) > 0) { }
            break;
        }
        if (child_pid > 0 && kde_screen_active()) {
            logmsg("desktop locked, stopping screensaver");
            stop_child();
        }
    }

    stop_child();
    if (notification) ext_idle_notification_v1_destroy(notification);
    if (notifier) ext_idle_notifier_v1_destroy(notifier);
    if (seat) wl_seat_destroy(seat);
    if (registry) wl_registry_destroy(registry);
    wl_display_disconnect(display);
#ifdef HAVE_SYSTEMD
    if (bus) sd_bus_flush_close_unref(bus);
#endif
    if (wake_fds[0] >= 0) close(wake_fds[0]);
    if (wake_fds[1] >= 0) close(wake_fds[1]);
    return 0;
}
