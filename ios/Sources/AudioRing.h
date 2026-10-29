#pragma once
#include <stdint.h>

typedef struct AudioRing AudioRing;
AudioRing *ring_create(uint32_t rate, uint32_t channels, uint32_t target_ms);
void ring_destroy(AudioRing *ring);
void ring_write(AudioRing *ring, const float *samples, uint32_t frames);
void ring_read(AudioRing *ring, float **outputs, uint32_t frames);
uint32_t ring_buffered(AudioRing *ring);
uint32_t ring_underruns(AudioRing *ring);
uint32_t ring_dropped(AudioRing *ring);
int parse_header(const uint8_t *bytes, uint32_t *rate, uint32_t *channels);
