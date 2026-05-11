#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/types.h>

int main(int argc, char *argv[]) {
    if (argc != 2) {
        fprintf(stderr, "Usage: pi-cam-power <shutdown|reboot>\n");
        return 1;
    }

    if (strcmp(argv[1], "shutdown") == 0) {
        execl("/usr/bin/systemctl", "systemctl", "poweroff", NULL);
    } else if (strcmp(argv[1], "reboot") == 0) {
        execl("/usr/bin/systemctl", "systemctl", "reboot", NULL);
    } else {
        fprintf(stderr, "Unknown command: %s\n", argv[1]);
        return 1;
    }

    perror("execl");
    return 1;
}
