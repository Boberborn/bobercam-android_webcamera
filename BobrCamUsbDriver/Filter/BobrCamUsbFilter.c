#include <initguid.h>
#include "BobrCamUsbFilter.h"

#define AOA_GET_PROTOCOL 51
#define AOA_SEND_STRING 52
#define AOA_START_ACCESSORY 53
#define AOA_MINIMUM_PROTOCOL 1

typedef struct _BOBRCAM_AOA_STRING
{
    const UCHAR* Buffer;
    ULONG Length;
} BOBRCAM_AOA_STRING;

static const UCHAR BobrCamManufacturer[] = "BobrCam";
static const UCHAR BobrCamModel[] = "BobrCam USB";
static const UCHAR BobrCamDescription[] = "Android camera for BobrCam";
static const UCHAR BobrCamVersion[] = "1.0";
static const UCHAR BobrCamUri[] =
    "https://github.com/Boberborn/bobercam";
static const UCHAR BobrCamSerial[] = "BobrCam";

static const BOBRCAM_AOA_STRING BobrCamIdentityStrings[] =
{
    { BobrCamManufacturer, sizeof(BobrCamManufacturer) },
    { BobrCamModel, sizeof(BobrCamModel) },
    { BobrCamDescription, sizeof(BobrCamDescription) },
    { BobrCamVersion, sizeof(BobrCamVersion) },
    { BobrCamUri, sizeof(BobrCamUri) },
    { BobrCamSerial, sizeof(BobrCamSerial) }
};

static
BOOLEAN
BobrCamIsAndroidVendor(
    _In_ USHORT VendorId
    );

static
NTSTATUS
BobrCamSendVendorControlTransfer(
    _In_ WDFUSBDEVICE UsbDevice,
    _In_ WDF_USB_BMREQUEST_DIRECTION Direction,
    _In_ UCHAR Request,
    _In_ USHORT Index,
    _Inout_updates_bytes_opt_(BufferLength) PVOID Buffer,
    _In_ ULONG BufferLength
    );

static
VOID
BobrCamForwardRequest(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target
    );

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, BobrCamEvtDeviceAdd)
#pragma alloc_text(PAGE, BobrCamEvtDevicePrepareHardware)
#pragma alloc_text(PAGE, BobrCamEvtDeviceReleaseHardware)
#pragma alloc_text(PAGE, BobrCamEvtIoDeviceControl)
#pragma alloc_text(PAGE, BobrCamStartAccessoryMode)
#pragma alloc_text(PAGE, BobrCamSendVendorControlTransfer)
#endif

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, BobrCamEvtDeviceAdd);
    return WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);
}

NTSTATUS
BobrCamEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_PNPPOWER_EVENT_CALLBACKS pnpCallbacks;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDFDEVICE device;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();

    WdfFdoInitSetFilter(DeviceInit);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpCallbacks);
    pnpCallbacks.EvtDevicePrepareHardware =
        BobrCamEvtDevicePrepareHardware;
    pnpCallbacks.EvtDeviceReleaseHardware =
        BobrCamEvtDeviceReleaseHardware;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpCallbacks);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &deviceAttributes,
        BOBRCAM_DEVICE_CONTEXT);
    deviceAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfDeviceCreate(
        &DeviceInit,
        &deviceAttributes,
        &device);
    if (!NT_SUCCESS(status))
        return status;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = BobrCamEvtIoDeviceControl;

    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
    if (!NT_SUCCESS(status))
        return status;

    return STATUS_SUCCESS;
}

NTSTATUS
BobrCamEvtDevicePrepareHardware(
    _In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesRaw,
    _In_ WDFCMRESLIST ResourcesTranslated
    )
{
    PBOBRCAM_DEVICE_CONTEXT context;
    WDF_USB_DEVICE_CREATE_CONFIG usbConfig;
    USB_DEVICE_DESCRIPTOR descriptor;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);
    PAGED_CODE();

    context = BobrCamGetDeviceContext(Device);
    if (context->UsbDevice != NULL)
        return STATUS_SUCCESS;

    WDF_USB_DEVICE_CREATE_CONFIG_INIT(
        &usbConfig,
        USBD_CLIENT_CONTRACT_VERSION_602);
    status = WdfUsbTargetDeviceCreateWithParameters(
        Device,
        &usbConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        &context->UsbDevice);
    if (!NT_SUCCESS(status))
    {
        context->UsbDevice = NULL;
        // This is a pass-through filter. Failure to create a USB target must
        // never prevent an unrelated composite device from starting.
        return STATUS_SUCCESS;
    }

    WdfUsbTargetDeviceGetDeviceDescriptor(
        context->UsbDevice,
        &descriptor);
    if (!BobrCamIsAndroidVendor(descriptor.idVendor))
    {
        WdfObjectDelete(context->UsbDevice);
        context->UsbDevice = NULL;
        return STATUS_SUCCESS;
    }

    if (!context->InterfaceCreated)
    {
        status = WdfDeviceCreateDeviceInterface(
            Device,
            &GUID_DEVINTERFACE_BOBRCAM_USB_FILTER,
            NULL);
        if (NT_SUCCESS(status))
            context->InterfaceCreated = TRUE;
    }

    return STATUS_SUCCESS;
}

NTSTATUS
BobrCamEvtDeviceReleaseHardware(
    _In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesTranslated
    )
{
    PBOBRCAM_DEVICE_CONTEXT context;

    UNREFERENCED_PARAMETER(ResourcesTranslated);
    PAGED_CODE();

    context = BobrCamGetDeviceContext(Device);
    if (context->UsbDevice != NULL)
    {
        WdfObjectDelete(context->UsbDevice);
        context->UsbDevice = NULL;
    }

    return STATUS_SUCCESS;
}

VOID
BobrCamEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    WDFDEVICE device;
    PBOBRCAM_DEVICE_CONTEXT context;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);
    PAGED_CODE();

    device = WdfIoQueueGetDevice(Queue);
    context = BobrCamGetDeviceContext(device);

    if (IoControlCode != IOCTL_BOBRCAM_USB_START_ACCESSORY)
    {
        BobrCamForwardRequest(
            Request,
            WdfDeviceGetIoTarget(device));
        return;
    }

    if (context->UsbDevice == NULL)
        status = STATUS_INVALID_DEVICE_STATE;
    else
        status = BobrCamStartAccessoryMode(context->UsbDevice);

    WdfRequestComplete(Request, status);
}

NTSTATUS
BobrCamStartAccessoryMode(
    _In_ WDFUSBDEVICE UsbDevice
    )
{
    USHORT protocol;
    ULONG index;
    NTSTATUS status;

    PAGED_CODE();

    protocol = 0;
    status = BobrCamSendVendorControlTransfer(
        UsbDevice,
        BmRequestDeviceToHost,
        AOA_GET_PROTOCOL,
        0,
        &protocol,
        sizeof(protocol));
    if (!NT_SUCCESS(status))
        return status;
    if (protocol < AOA_MINIMUM_PROTOCOL)
        return STATUS_NOT_SUPPORTED;

    for (index = 0;
         index < RTL_NUMBER_OF(BobrCamIdentityStrings);
         index++)
    {
        status = BobrCamSendVendorControlTransfer(
            UsbDevice,
            BmRequestHostToDevice,
            AOA_SEND_STRING,
            (USHORT)index,
            (PVOID)BobrCamIdentityStrings[index].Buffer,
            BobrCamIdentityStrings[index].Length);
        if (!NT_SUCCESS(status))
            return status;
    }

    status = BobrCamSendVendorControlTransfer(
        UsbDevice,
        BmRequestHostToDevice,
        AOA_START_ACCESSORY,
        0,
        NULL,
        0);
    if (status == STATUS_DEVICE_NOT_CONNECTED ||
        status == STATUS_DELETE_PENDING)
    {
        return STATUS_SUCCESS;
    }

    return status;
}

static
NTSTATUS
BobrCamSendVendorControlTransfer(
    _In_ WDFUSBDEVICE UsbDevice,
    _In_ WDF_USB_BMREQUEST_DIRECTION Direction,
    _In_ UCHAR Request,
    _In_ USHORT Index,
    _Inout_updates_bytes_opt_(BufferLength) PVOID Buffer,
    _In_ ULONG BufferLength
    )
{
    WDF_USB_CONTROL_SETUP_PACKET setupPacket;
    WDF_MEMORY_DESCRIPTOR memoryDescriptor;
    PWDF_MEMORY_DESCRIPTOR memoryDescriptorPointer;
    WDF_REQUEST_SEND_OPTIONS options;

    PAGED_CODE();

    WDF_USB_CONTROL_SETUP_PACKET_INIT_VENDOR(
        &setupPacket,
        Direction,
        BmRequestToDevice,
        Request,
        0,
        Index);

    memoryDescriptorPointer = NULL;
    if (Buffer != NULL && BufferLength > 0)
    {
        WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(
            &memoryDescriptor,
            Buffer,
            BufferLength);
        memoryDescriptorPointer = &memoryDescriptor;
    }

    WDF_REQUEST_SEND_OPTIONS_INIT(
        &options,
        WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(
        &options,
        WDF_REL_TIMEOUT_IN_SEC(3));

    return WdfUsbTargetDeviceSendControlTransferSynchronously(
        UsbDevice,
        WDF_NO_HANDLE,
        &options,
        &setupPacket,
        memoryDescriptorPointer,
        NULL);
}

static
BOOLEAN
BobrCamIsAndroidVendor(
    _In_ USHORT VendorId
    )
{
    static const USHORT AndroidVendorIds[] =
    {
        0x0409, // NEC
        0x04E8, // Samsung
        0x0502, // Acer
        0x05C6, // Qualcomm
        0x091E, // Garmin-Asus
        0x0955, // NVIDIA
        0x0B05, // ASUS
        0x0BB4, // HTC
        0x0E8D, // MediaTek
        0x0FCE, // Sony
        0x1004, // LG
        0x12D1, // Huawei
        0x17EF, // Lenovo
        0x18D1, // Google
        0x1949, // Amazon
        0x19D2, // ZTE
        0x1EBF, // Geeksphone
        0x22B8, // Motorola
        0x22D9, // OPPO / Realme
        0x2717, // Xiaomi
        0x2A45, // Meizu
        0x2A70, // OnePlus
        0x2AE5, // Fairphone
        0x2D95, // Vivo
        0x2E04  // HMD / Nokia
    };
    ULONG index;

    for (index = 0;
         index < RTL_NUMBER_OF(AndroidVendorIds);
         index++)
    {
        if (AndroidVendorIds[index] == VendorId)
            return TRUE;
    }

    return FALSE;
}

static
VOID
BobrCamForwardRequest(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target
    )
{
    WDF_REQUEST_SEND_OPTIONS options;

    WDF_REQUEST_SEND_OPTIONS_INIT(
        &options,
        WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, Target, &options))
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
}
