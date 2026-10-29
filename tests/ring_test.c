#include "../ios/Sources/AudioRing.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    uint8_t header[] = {'U','S','P','K',1,0,0,0,0x80,0xbb,0,0,2,0,0,0};
    uint32_t rate, channels;
    assert(parse_header(header, &rate, &channels));
    assert(rate == 48000 && channels == 2);
    header[4] = 2;
    assert(!parse_header(header, &rate, &channels));
    header[4] = 1; header[12] = 0;
    assert(!parse_header(header, &rate, &channels));

    AudioRing *r = ring_create(48000, 2, 8);
    float input[4800 * 2];
    float left[480], right[480];
    float *output[] = {left, right};
    for (int i = 0; i < 4800; i++) { input[i*2] = (float)i; input[i*2+1] = -(float)i; }
    ring_read(r, output, 240);
    assert(left[0] == 0 && right[239] == 0);
    ring_write(r, input, 480);
    ring_read(r, output, 240);
    assert(ring_buffered(r) == 480); // Wait for target + one render block.
    ring_write(r, input + 960, 480);
    ring_read(r, output, 240);
    assert(left[239] == 239 && right[239] == -239);
    ring_read(r, output, 480);
    ring_read(r, output, 480);
    assert(left[239] == 959 && left[240] == 0);
    assert(ring_underruns(r) == 1);
    ring_write(r, input, 4800);
    ring_read(r, output, 240);
    assert(ring_buffered(r) == 384);
    assert(ring_dropped(r) == 4176);
    assert(left[0] == 4176 && right[239] == -4415);
    ring_destroy(r);

    r = ring_create(48000, 2, 0);
    // Exercise wrap-around repeatedly without changing sample order.
    for (int i = 0; i < 120; i++) {
        ring_write(r, input, 480);
        ring_read(r, output, 480);
        assert(left[479] == 479 && right[479] == -479);
    }
    for (int i = 0; i < 5; i++) ring_write(r, input, 4800);
    ring_write(r, input, 480);
    assert(ring_buffered(r) == 24000);
    assert(ring_dropped(r) == 480);
    ring_destroy(r);
    puts("Audio ring and protocol tests passed.");
    return 0;
}
