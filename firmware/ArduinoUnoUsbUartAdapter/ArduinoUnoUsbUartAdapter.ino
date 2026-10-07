/*
  Transparent USB <-> UART adapter for Arduino Uno.

  USB side:   hardware Serial through the Uno USB interface
  Model side: SoftwareSerial on pins 10 (RX) and 11 (TX)

  The sketch does not parse frames, calculate CRC or generate ACK messages.
  All protocol logic lives in the .NET bridge.
*/

#include <Arduino.h>
#include <SoftwareSerial.h>

static constexpr uint8_t MODEL_RX_PIN = 10;
static constexpr uint8_t MODEL_TX_PIN = 11;
static constexpr unsigned long BAUD_RATE = 2400;

SoftwareSerial modelSerial(MODEL_RX_PIN, MODEL_TX_PIN);

void setup()
{
  Serial.begin(BAUD_RATE);
  modelSerial.begin(BAUD_RATE);
  modelSerial.listen();
}

void loop()
{
  // Forward at most one byte in each direction per iteration so neither
  // direction can permanently starve the other one.
  if (Serial.available() > 0)
  {
    modelSerial.write((uint8_t)Serial.read());
  }

  if (modelSerial.available() > 0 && Serial.availableForWrite() > 0)
  {
    Serial.write((uint8_t)modelSerial.read());
  }
}
