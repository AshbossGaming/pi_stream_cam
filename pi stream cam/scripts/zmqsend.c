#include <zmq.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "Usage: zmqsend <command> [<command>...] [<address>]\n");
        return 1;
    }

    void *ctx = zmq_ctx_new();
    if (!ctx) {
        fprintf(stderr, "Failed to create ZMQ context\n");
        return 1;
    }

    void *socket = zmq_socket(ctx, ZMQ_REQ);
    if (!socket) {
        fprintf(stderr, "Failed to create ZMQ socket\n");
        zmq_ctx_destroy(ctx);
        return 1;
    }

    char *host = "tcp://localhost:5555";
    int last_arg = argc - 1;

    if (strncmp(argv[last_arg], "tcp://", 6) == 0 ||
        strncmp(argv[last_arg], "ipc://", 6) == 0 ||
        strncmp(argv[last_arg], "inproc://", 9) == 0) {
        host = argv[last_arg];
        last_arg--;
    }

    int timeout = 2000;
    zmq_setsockopt(socket, ZMQ_RCVTIMEO, &timeout, sizeof(timeout));
    zmq_setsockopt(socket, ZMQ_SNDTIMEO, &timeout, sizeof(timeout));

    if (zmq_connect(socket, host) != 0) {
        fprintf(stderr, "Failed to connect to %s\n", host);
        zmq_close(socket);
        zmq_ctx_destroy(ctx);
        return 1;
    }

    for (int i = 1; i <= last_arg; i++) {
        zmq_msg_t msg;
        size_t len = strlen(argv[i]);
        zmq_msg_init_size(&msg, len);
        memcpy(zmq_msg_data(&msg), argv[i], len);

        if (zmq_msg_send(&msg, socket, 0) == -1) {
            fprintf(stderr, "Failed to send: %s\n", argv[i]);
            zmq_msg_close(&msg);
            continue;
        }
        zmq_msg_close(&msg);

        zmq_msg_t reply;
        zmq_msg_init(&reply);
        zmq_msg_recv(&reply, socket, 0);
        zmq_msg_close(&reply);
    }

    zmq_close(socket);
    zmq_ctx_destroy(ctx);
    return 0;
}
