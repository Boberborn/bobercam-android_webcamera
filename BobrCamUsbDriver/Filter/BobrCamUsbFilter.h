#pragma once

#include <ntddk.h>
#include <usbdi.h>
#include <usbdlib.h>
#include <wdf.h>
#include <wdfusb.h>

#include "BobrCamUsbFilterPublic.h"

typedef struct _BOBRCAM_DEVICE_CONTEXT
{
    WDFUSBDEVICE UsbDevice;
    BOOLEAN InterfaceCreated;
} BOBRCAM_DEVICE_CONTEXT, *PBOBRCAM_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(
    BOBRCAM_DEVICE_CONTEXT,
    BobrCamGetDeviceContext);

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD BobrCamEvtDeviceAdd;
EVT_WDF_DEVICE_PREPARE_HARDWARE BobrCamEvtDevicePrepareHardware;
EVT_WDF_DEVICE_RELEASE_HARDWARE BobrCamEvtDeviceReleaseHardware;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL BobrCamEvtIoDeviceControl;

NTSTATUS
BobrCamStartAccessoryMode(
    _In_ WDFUSBDEVICE UsbDevice
    );
