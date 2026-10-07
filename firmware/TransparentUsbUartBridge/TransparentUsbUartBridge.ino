/*
  Transparent USB <-> UART converter for boards exposing Serial and Serial1.
  All protocol parsing, CRC, ACK, retry and Millumin logic lives in the .NET bridge.
*/

#include <Arduino.h>

#if !defined(HAVE_HWSERIAL1)
#error "This sketch requires a board with Serial1 (for example Arduino Mega or Leonardo)."
#endif

static constexpr unsigned long BAUD_RATE = 2400;

void setup()
{
  Serial.begin(BAUD_RATE);
  Serial1.begin(BAUD_RATE, SERIAL_8N1);
}

void loop()
{
  while (Serial.available() > 0 && Serial1.availableForWrite() > 0)
  {
    Serial1.write((uint8_t)Serial.read());
  }

  while (Serial1.available() > 0 && Serial.availableForWrite() > 0)
  {
    Serial.write((uint8_t)Serial1.read());
  }
}
