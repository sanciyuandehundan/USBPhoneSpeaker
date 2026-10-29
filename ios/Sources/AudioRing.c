#include "AudioRing.h"
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

struct AudioRing {
    float *samples;
    uint32_t capacity, channels, target, maximum;
    _Atomic uint64_t read, write;
    _Atomic uint32_t underruns, dropped;
    int primed;
};

AudioRing *ring_create(uint32_t rate, uint32_t channels, uint32_t target_ms) {
    AudioRing *r = calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->capacity = rate / 2;
    r->channels = channels;
    r->target = rate * target_ms / 1000;
    r->maximum = rate * (target_ms + 25) / 1000;
    atomic_init(&r->read, 0);
    atomic_init(&r->write, 0);
    atomic_init(&r->underruns, 0);
    atomic_init(&r->dropped, 0);
    r->samples = calloc((size_t)r->capacity * channels, sizeof(float));
    if (!r->samples) { free(r); return NULL; }
    return r;
}

void ring_destroy(AudioRing *r) { free(r->samples); free(r); }

void ring_write(AudioRing *r, const float *samples, uint32_t frames) {
    uint64_t write = atomic_load_explicit(&r->write, memory_order_relaxed);
    uint64_t read = atomic_load_explicit(&r->read, memory_order_acquire);
    if (frames > r->capacity - (write - read)) {
        atomic_fetch_add_explicit(&r->dropped, frames, memory_order_relaxed);
        return;
    }
    for (uint32_t i = 0; i < frames; i++) {
        memcpy(r->samples + ((write + i) % r->capacity) * r->channels,
               samples + i * r->channels, r->channels * sizeof(float));
    }
    atomic_store_explicit(&r->write, write + frames, memory_order_release);
}

void ring_read(AudioRing *r, float **outputs, uint32_t frames) {
    uint64_t read = atomic_load_explicit(&r->read, memory_order_relaxed);
    uint64_t write = atomic_load_explicit(&r->write, memory_order_acquire);
    uint64_t available = write - read;
    if (available > r->maximum && available > frames + r->target) {
        uint64_t discard = available - frames - r->target;
        read += discard;
        available -= discard;
        atomic_fetch_add_explicit(&r->dropped, (uint32_t)discard, memory_order_relaxed);
    }
    if (!r->primed && available >= r->target + frames) r->primed = 1;
    uint32_t take = r->primed ? (available < frames ? (uint32_t)available : frames) : 0;
    for (uint32_t c = 0; c < r->channels; c++) {
        for (uint32_t i = 0; i < take; i++)
            outputs[c][i] = r->samples[((read + i) % r->capacity) * r->channels + c];
        memset(outputs[c] + take, 0, (frames - take) * sizeof(float));
    }
    atomic_store_explicit(&r->read, read + take, memory_order_release);
    if (r->primed && take < frames) {
        r->primed = 0;
        atomic_fetch_add_explicit(&r->underruns, 1, memory_order_relaxed);
    }
}

uint32_t ring_buffered(AudioRing *r) {
    // UI reads concurrently with both ends; it must not report an unsigned underflow.
    uint64_t read = atomic_load_explicit(&r->read, memory_order_acquire);
    uint64_t write = atomic_load_explicit(&r->write, memory_order_acquire);
    return write >= read ? (uint32_t)(write - read) : 0;
}
uint32_t ring_underruns(AudioRing *r) { return atomic_load_explicit(&r->underruns, memory_order_relaxed); }
uint32_t ring_dropped(AudioRing *r) { return atomic_load_explicit(&r->dropped, memory_order_relaxed); }

static uint32_t little32(const uint8_t *p) {
    return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24;
}
int parse_header(const uint8_t *bytes, uint32_t *rate, uint32_t *channels) {
    if (memcmp(bytes, "USPK", 4) || little32(bytes + 4) != 1) return 0;
    *rate = little32(bytes + 8);
    *channels = little32(bytes + 12);
    return *rate >= 8000 && *rate <= 192000 && *channels >= 1 && *channels <= 8;
}
