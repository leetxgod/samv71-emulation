/*******************************************************************************
  MPLAB Harmony Application Source File
*******************************************************************************/

#include "app.h"
#include <string.h>

// *****************************************************************************
// Application Data
// *****************************************************************************

APP_DATA appData;

// RX buffer ? holds incoming characters until a full line arrives
#define RX_BUFFER_SIZE  128
static char rxBuffer[RX_BUFFER_SIZE];
static uint16_t rxIndex = 0;

// *****************************************************************************
// Section: Application Initialization and State Machine Functions
// *****************************************************************************

void APP_Initialize(void)
{
    appData.state = APP_STATE_INIT;
    memset(rxBuffer, 0, sizeof(rxBuffer));
    rxIndex = 0;
}

void APP_Tasks(void)
{
    switch (appData.state)
    {
        case APP_STATE_INIT:
        {
            bool appInitialized = true;

            if (appInitialized)
            {
                printf("UART Echo ready. Type something!\r\n");
                appData.state = APP_STATE_SERVICE_TASKS;
            }
            break;
        }

        case APP_STATE_SERVICE_TASKS:
        {
            // --- Periodic heartbeat print ---
            printf("Hey Caralho From FreeRTOS!\r\n");

            // --- UART RX: read all available bytes this tick ---
            // UART0_Read returns number of bytes actually read.
            // We read one byte at a time to keep it simple.
            uint8_t rxByte;
            while (UART0_Read(&rxByte, 1) == 1)
            {
                // Echo the character back so the user sees what they typed
                UART0_Write(&rxByte, 1);

                if (rxByte == '\r' || rxByte == '\n')
                {
                    // Null-terminate and print the complete line
                    rxBuffer[rxIndex] = '\0';
                    if (rxIndex > 0)
                    {
                        printf("\r\nReceived: %s\r\n", rxBuffer);
                    }
                    rxIndex = 0;
                }
                else if (rxIndex < RX_BUFFER_SIZE - 1)
                {
                    rxBuffer[rxIndex++] = (char)rxByte;
                }
                else
                {
                    // Buffer full ? flush it and warn
                    rxBuffer[rxIndex] = '\0';
                    printf("\r\n[WARN] RX buffer full, flushing: %s\r\n", rxBuffer);
                    rxIndex = 0;
                }
            }

            vTaskDelay(pdMS_TO_TICKS(1000U));
            break;
        }

        default:
            break;
    }
}

/*******************************************************************************
 End of File
 */