/* Native vkd3d-proton ABI probe. Compile against the pinned source's generated headers. */
#define INITGUID
#include <vkd3d.h>
#ifndef VKD3D_NATIVE_API
#error "The pinned visibility-patched vkd3d.h is required"
#endif
#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <time.h>
#include <stddef.h>
#include <stdlib.h>

#define ABI(iface, method, result, ...) \
    _Static_assert(__builtin_types_compatible_p(__typeof__(((iface##Vtbl *)0)->method), \
        result (*)(__VA_ARGS__)), #iface "::" #method " ABI");
ABI(ID3D12Heap, GetDesc, D3D12_HEAP_DESC *, ID3D12Heap *, D3D12_HEAP_DESC *)
ABI(ID3D12Resource, GetDesc, D3D12_RESOURCE_DESC *, ID3D12Resource *, D3D12_RESOURCE_DESC *)
ABI(ID3D12DescriptorHeap, GetDesc, D3D12_DESCRIPTOR_HEAP_DESC *, ID3D12DescriptorHeap *, D3D12_DESCRIPTOR_HEAP_DESC *)
ABI(ID3D12DescriptorHeap, GetCPUDescriptorHandleForHeapStart, D3D12_CPU_DESCRIPTOR_HANDLE *, ID3D12DescriptorHeap *, D3D12_CPU_DESCRIPTOR_HANDLE *)
ABI(ID3D12DescriptorHeap, GetGPUDescriptorHandleForHeapStart, D3D12_GPU_DESCRIPTOR_HANDLE *, ID3D12DescriptorHeap *, D3D12_GPU_DESCRIPTOR_HANDLE *)
ABI(ID3D12CommandQueue, GetDesc, D3D12_COMMAND_QUEUE_DESC *, ID3D12CommandQueue *, D3D12_COMMAND_QUEUE_DESC *)
ABI(ID3D12Device, GetResourceAllocationInfo, D3D12_RESOURCE_ALLOCATION_INFO *, ID3D12Device *, D3D12_RESOURCE_ALLOCATION_INFO *, UINT, UINT, const D3D12_RESOURCE_DESC *)
ABI(ID3D12Device, GetCustomHeapProperties, D3D12_HEAP_PROPERTIES *, ID3D12Device *, D3D12_HEAP_PROPERTIES *, UINT, D3D12_HEAP_TYPE)
ABI(ID3D12Device, GetAdapterLuid, LUID *, ID3D12Device *, LUID *)
ABI(ID3D12Device4, GetResourceAllocationInfo1, D3D12_RESOURCE_ALLOCATION_INFO *, ID3D12Device4 *, D3D12_RESOURCE_ALLOCATION_INFO *, UINT, UINT, const D3D12_RESOURCE_DESC *, D3D12_RESOURCE_ALLOCATION_INFO1 *)
ABI(ID3D12DeviceConfiguration, GetDesc, D3D12_DEVICE_CONFIGURATION_DESC *, ID3D12DeviceConfiguration *, D3D12_DEVICE_CONFIGURATION_DESC *)

/* These are the x64 native buffer layouts consumed by LinuxAdapters.fs. */
#define SIZE(type, bytes) _Static_assert(sizeof(type) == bytes, #type " size")
#define OFFSET(type, field, bytes) _Static_assert(offsetof(type, field) == bytes, #type "::" #field " offset")
SIZE(void *, 8);
SIZE(BOOL, 4);
SIZE(WCHAR, 2);
SIZE(LONG, 4);
SIZE(LUID, 8);
SIZE(D3D12_CPU_DESCRIPTOR_HANDLE, 8);
SIZE(D3D12_GPU_DESCRIPTOR_HANDLE, 8);
SIZE(struct vkd3d_instance_create_info, 40);
OFFSET(struct vkd3d_instance_create_info, pfn_vkGetInstanceProcAddr, 0);
SIZE(struct vkd3d_device_create_info, 88);
OFFSET(struct vkd3d_device_create_info, minimum_feature_level, 0);
OFFSET(struct vkd3d_device_create_info, instance, 8);
OFFSET(struct vkd3d_device_create_info, vk_physical_device, 24);
OFFSET(struct vkd3d_device_create_info, adapter_luid, 72);
OFFSET(struct vkd3d_device_create_info, independent, 84);
SIZE(VkPhysicalDeviceProperties2, 840);
OFFSET(VkPhysicalDeviceProperties2, pNext, 8);
OFFSET(VkPhysicalDeviceProperties2, properties, 16);
OFFSET(VkPhysicalDeviceProperties, apiVersion, 0);
OFFSET(VkPhysicalDeviceProperties, driverVersion, 4);
OFFSET(VkPhysicalDeviceProperties, vendorID, 8);
OFFSET(VkPhysicalDeviceProperties, deviceID, 12);
OFFSET(VkPhysicalDeviceProperties, deviceType, 16);
OFFSET(VkPhysicalDeviceProperties, deviceName, 20);
SIZE(VkPhysicalDeviceIDProperties, 64);
OFFSET(VkPhysicalDeviceIDProperties, deviceUUID, 16);
OFFSET(VkPhysicalDeviceIDProperties, deviceLUID, 48);
OFFSET(VkPhysicalDeviceIDProperties, deviceLUIDValid, 60);
SIZE(VkPhysicalDeviceDriverProperties, 536);
OFFSET(VkPhysicalDeviceDriverProperties, driverName, 20);
OFFSET(VkPhysicalDeviceDriverProperties, driverInfo, 276);
SIZE(VkPhysicalDeviceMemoryProperties, 520);
OFFSET(VkPhysicalDeviceMemoryProperties, memoryHeapCount, 260);
OFFSET(VkPhysicalDeviceMemoryProperties, memoryHeaps, 264);
SIZE(VkMemoryHeap, 16);
OFFSET(VkMemoryHeap, size, 0);
OFFSET(VkMemoryHeap, flags, 8);

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}

static void *signal_fence(void *arg)
{
    ID3D12Fence *f = arg;
    const double until = now() + 0.1;
    while (now() < until)
        sched_yield();
    f->lpVtbl->Signal(f, 1);
    return NULL;
}

int main(int argc, char **argv)
{
    if (argc != 3) return 2;
    void *core = dlopen(argv[1], RTLD_NOW | RTLD_GLOBAL);
    void *lib = dlopen(argv[2], RTLD_NOW | RTLD_LOCAL);
    if (!core || !lib) { fprintf(stderr, "%s\n", dlerror()); return 3; }
    printf("eleven struct-return prototypes: explicit self, return pointer; compiler assertions PASS\n");
    printf("BOOL/LONG=4 WCHAR=2 handles/LUID=8; Linux native buffer layout assertions PASS\n");
    printf("vkd3d_create_device exported: %s\n", dlsym(core, "vkd3d_create_device") ? "yes" : "no");
    printf("device_create_info size=%zu independent=%zu flags=%zu luid=%zu\n",
        sizeof(struct vkd3d_device_create_info), offsetof(struct vkd3d_device_create_info, independent),
        offsetof(struct vkd3d_device_create_info, device_factory_flags), offsetof(struct vkd3d_device_create_info, adapter_luid));
    HRESULT (*create)(const struct vkd3d_device_create_info *, REFIID, void **) = dlsym(core, "vkd3d_create_device");
    HRESULT (*instance_create)(const struct vkd3d_instance_create_info *, struct vkd3d_instance **) = dlsym(core, "vkd3d_create_instance");
    VkInstance (*instance_get)(struct vkd3d_instance *) = dlsym(core, "vkd3d_instance_get_vk_instance");
    ULONG (*instance_release)(struct vkd3d_instance *) = dlsym(core, "vkd3d_instance_decref");
    if (!create || !instance_create || !instance_get || !instance_release) return 4;
    struct vkd3d_instance *instance = NULL;
    struct vkd3d_instance_create_info instance_info = {0};
    void *vulkan = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
    PFN_vkGetInstanceProcAddr get_proc = dlsym(vulkan, "vkGetInstanceProcAddr");
    instance_info.pfn_vkGetInstanceProcAddr = get_proc;
    if (FAILED(instance_create(&instance_info, &instance))) return 8;
    VkInstance vk_instance = instance_get(instance);
    PFN_vkEnumeratePhysicalDevices enumerate = (PFN_vkEnumeratePhysicalDevices)get_proc(vk_instance, "vkEnumeratePhysicalDevices");
    uint32_t count = 0;
    if (enumerate(vk_instance, &count, NULL) || !count) return 9;
    VkPhysicalDevice *physical = calloc(count, sizeof(*physical));
    if (enumerate(vk_instance, &count, physical)) return 10;
    printf("selected VkPhysicalDevice=%p of %u\n", (void *)physical[0], count);
    printf("Vulkan layouts: properties2=%zu properties=%zu ID=%zu driver=%zu memory=%zu heaps=%zu\n",
        sizeof(VkPhysicalDeviceProperties2), offsetof(VkPhysicalDeviceProperties2, properties),
        sizeof(VkPhysicalDeviceIDProperties), sizeof(VkPhysicalDeviceDriverProperties),
        sizeof(VkPhysicalDeviceMemoryProperties), offsetof(VkPhysicalDeviceMemoryProperties, memoryHeaps));
    struct vkd3d_device_create_info device_info = {0};
    device_info.minimum_feature_level = D3D_FEATURE_LEVEL_11_0;
    device_info.instance = instance;
    device_info.vk_physical_device = physical[0];
    device_info.independent = true;
    ID3D12Device *dev = NULL;
    double start = now();
    HRESULT hr = create(&device_info, &IID_ID3D12Device, (void **)&dev);
    free(physical);
    instance_release(instance);
    printf("first-device-wall=%.9fs HRESULT=%#x device=%p\n", now() - start, (unsigned)hr, (void *)dev);
    fflush(stdout);
    if (FAILED(hr) || !dev) return 5;
    D3D12_FEATURE_DATA_SHADER_MODEL sm = { D3D_SHADER_MODEL_6_6 };
    hr = dev->lpVtbl->CheckFeatureSupport(dev, D3D12_FEATURE_SHADER_MODEL, &sm, sizeof(sm));
    printf("SHADER_MODEL hr=%#x value=%#x\n", (unsigned)hr, sm.HighestShaderModel);
    D3D12_FEATURE_DATA_D3D12_OPTIONS options = {0};
    hr = dev->lpVtbl->CheckFeatureSupport(dev, D3D12_FEATURE_D3D12_OPTIONS, &options, sizeof(options));
    printf("OPTIONS hr=%#x binding-tier=%u\n", (unsigned)hr, options.ResourceBindingTier);
#define QUERY(feature, type) do { \
    type data = {0}; \
    hr = dev->lpVtbl->CheckFeatureSupport(dev, feature, &data, sizeof(data)); \
    printf(#feature " hr=%#x\n", (unsigned)hr); \
} while (0)
    QUERY(D3D12_FEATURE_D3D12_OPTIONS1, D3D12_FEATURE_DATA_D3D12_OPTIONS1);
    QUERY(D3D12_FEATURE_D3D12_OPTIONS4, D3D12_FEATURE_DATA_D3D12_OPTIONS4);
    QUERY(D3D12_FEATURE_D3D12_OPTIONS9, D3D12_FEATURE_DATA_D3D12_OPTIONS9);
    QUERY(D3D12_FEATURE_D3D12_OPTIONS11, D3D12_FEATURE_DATA_D3D12_OPTIONS11);
    QUERY(D3D12_FEATURE_D3D12_OPTIONS12, D3D12_FEATURE_DATA_D3D12_OPTIONS12);
    QUERY(D3D12_FEATURE_ARCHITECTURE1, D3D12_FEATURE_DATA_ARCHITECTURE1);
#undef QUERY
    ID3D12Fence *fence = NULL;
    hr = dev->lpVtbl->CreateFence(dev, 0, D3D12_FENCE_FLAG_NONE, &IID_ID3D12Fence, (void **)&fence);
    if (FAILED(hr)) return 6;
    pthread_t thread;
    if (pthread_create(&thread, NULL, signal_fence, fence)) return 7;
    start = now();
    hr = fence->lpVtbl->SetEventOnCompletion(fence, 1, NULL);
    double elapsed = now() - start;
    UINT64 completed = fence->lpVtbl->GetCompletedValue(fence);
    printf("SetEventOnCompletion(1,NULL) hr=%#x wall=%.9fs completed=%llu\n", (unsigned)hr, elapsed, (unsigned long long)completed);
    pthread_join(thread, NULL);
    fence->lpVtbl->Release(fence);
    dev->lpVtbl->Release(dev);
    return FAILED(hr) || completed != 1 || elapsed < 0.05;
}
