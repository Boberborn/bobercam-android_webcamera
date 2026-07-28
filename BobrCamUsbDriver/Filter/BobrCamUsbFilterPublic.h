#pragma once

#include <guiddef.h>
#include <devioctl.h>

// {A2C43F18-7E80-46E7-B9B9-5D372D00B861}
DEFINE_GUID(
    GUID_DEVINTERFACE_BOBRCAM_USB_FILTER,
    0xa2c43f18,
    0x7e80,
    0x46e7,
    0xb9,
    0xb9,
    0x5d,
    0x37,
    0x2d,
    0x00,
    0xb8,
    0x61);

#define IOCTL_BOBRCAM_USB_START_ACCESSORY \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, \
             FILE_READ_DATA | FILE_WRITE_DATA)
