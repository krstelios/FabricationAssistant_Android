// draco_native.cpp - minimal C-callable wrapper around Google's Draco decoder.
// Exports the entry points consumed by FabricationAssistant.Draco.Android's
// DracoNativeDecoder P/Invoke layer:
//   draco_decode_buffer_to_mesh   - decodes a Draco buffer to an opaque mesh handle
//   draco_mesh_get_num_faces      - returns the triangle count
//   draco_mesh_get_num_points     - returns the point/vertex count
//   draco_mesh_copy_attribute_float - copies POSITION / NORMAL / TEX_COORD as floats
//   draco_mesh_get_attribute_info_by_unique_id - returns a Draco attribute's layout
//   draco_mesh_copy_attribute_by_unique_id - copies a Draco attribute as raw values
//   draco_mesh_copy_indices_uint32  - copies triangle indices as uint32 triples
//   draco_mesh_destroy            - frees the opaque handle

#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <draco/compression/decode.h>
#include <draco/mesh/mesh.h>
#include <draco/attributes/geometry_attribute.h>
#include <draco/core/draco_types.h>

// The shared library is compiled with -fvisibility=hidden to keep Draco's
// internals out of the .dynsym table. We need to explicitly mark our extern
// "C" entry points as visible so the JNI runtime / dlsym can find them.
#define DRACO_NATIVE_EXPORT __attribute__((visibility("default")))

extern "C" {

// Opaque mesh handle returned to managed code. We allocate on the heap and
// hand the pointer back. The managed side calls draco_mesh_destroy when done.
struct DracoMeshHandle {
    std::unique_ptr<draco::Mesh> mesh;
};

struct DracoAttributeInfo {
    int32_t attribute_type;
    int32_t data_type;
    int32_t component_count;
    int32_t normalized;
    int32_t byte_stride;
};

DRACO_NATIVE_EXPORT
DracoMeshHandle* draco_decode_buffer_to_mesh(const uint8_t* data, int32_t size) {
    if (!data || size <= 0) return nullptr;
    draco::DecoderBuffer buffer;
    buffer.Init(reinterpret_cast<const char*>(data), static_cast<size_t>(size));

    draco::Decoder decoder;
    auto status_or = decoder.DecodeMeshFromBuffer(&buffer);
    if (!status_or.ok()) return nullptr;
    auto handle = new DracoMeshHandle();
    handle->mesh = std::move(status_or).value();
    return handle;
}

DRACO_NATIVE_EXPORT
int32_t draco_mesh_get_num_faces(const DracoMeshHandle* h) {
    if (!h || !h->mesh) return 0;
    auto count = h->mesh->num_faces();
    if (count > static_cast<uint32_t>(std::numeric_limits<int32_t>::max())) return -1;
    return static_cast<int32_t>(count);
}

DRACO_NATIVE_EXPORT
int32_t draco_mesh_get_num_points(const DracoMeshHandle* h) {
    if (!h || !h->mesh) return 0;
    auto count = h->mesh->num_points();
    if (count > static_cast<uint32_t>(std::numeric_limits<int32_t>::max())) return -1;
    return static_cast<int32_t>(count);
}

// Copies a named attribute as float components. attribute_type: 0=POSITION,
// 1=NORMAL, 2=TEX_COORD. components_expected is the per-point component count
// (3 for VEC3, 2 for VEC2). out_count is the float capacity of `out`. Returns
// 1 on success, 0 if attribute absent or output buffer too small.
DRACO_NATIVE_EXPORT
int32_t draco_mesh_copy_attribute_float(
    const DracoMeshHandle* h,
    int32_t attribute_type,
    int32_t components_expected,
    float* out,
    int32_t out_count) {
    if (!h || !h->mesh || !out) return 0;
    draco::GeometryAttribute::Type type =
        attribute_type == 0 ? draco::GeometryAttribute::POSITION :
        attribute_type == 1 ? draco::GeometryAttribute::NORMAL :
        attribute_type == 2 ? draco::GeometryAttribute::TEX_COORD :
        draco::GeometryAttribute::INVALID;
    const auto* attr = h->mesh->GetNamedAttribute(type);
    if (!attr) return 0;
    int64_t required = static_cast<int64_t>(h->mesh->num_points()) * components_expected;
    if (required <= 0 || required > std::numeric_limits<int32_t>::max()) return 0;
    if (static_cast<int64_t>(out_count) < required) return 0;

    float* p = out;
    for (draco::PointIndex i(0); i < h->mesh->num_points(); ++i) {
        if (!attr->ConvertValue<float>(attr->mapped_index(i), components_expected, p)) return 0;
        p += components_expected;
    }
    return 1;
}

DRACO_NATIVE_EXPORT
int32_t draco_mesh_get_attribute_info_by_unique_id(
    const DracoMeshHandle* h,
    int32_t unique_id,
    DracoAttributeInfo* out_info) {
    if (!h || !h->mesh || unique_id < 0 || !out_info) return 0;

    const auto* attr = h->mesh->GetAttributeByUniqueId(static_cast<uint32_t>(unique_id));
    if (!attr) return 0;

    int32_t type_length = draco::DataTypeLength(attr->data_type());
    if (type_length <= 0) return 0;

    int64_t tight_stride = static_cast<int64_t>(type_length) * attr->num_components();
    if (tight_stride <= 0 || tight_stride > std::numeric_limits<int32_t>::max()) return 0;

    out_info->attribute_type = static_cast<int32_t>(attr->attribute_type());
    out_info->data_type = static_cast<int32_t>(attr->data_type());
    out_info->component_count = static_cast<int32_t>(attr->num_components());
    out_info->normalized = attr->normalized() ? 1 : 0;
    out_info->byte_stride = static_cast<int32_t>(tight_stride);
    return 1;
}

DRACO_NATIVE_EXPORT
int32_t draco_mesh_copy_attribute_by_unique_id(
    const DracoMeshHandle* h,
    int32_t unique_id,
    uint8_t* out,
    int32_t out_count) {
    if (!h || !h->mesh || unique_id < 0 || !out) return 0;

    const auto* attr = h->mesh->GetAttributeByUniqueId(static_cast<uint32_t>(unique_id));
    if (!attr) return 0;

    int32_t type_length = draco::DataTypeLength(attr->data_type());
    if (type_length <= 0) return 0;

    int64_t bytes_per_point = static_cast<int64_t>(type_length) * attr->num_components();
    if (bytes_per_point <= 0 || bytes_per_point > std::numeric_limits<int32_t>::max()) return 0;

    int64_t point_count = static_cast<int64_t>(h->mesh->num_points());
    int64_t required = point_count * bytes_per_point;
    if (required <= 0 || required > std::numeric_limits<int32_t>::max()) return 0;
    if (static_cast<int64_t>(out_count) < required) return 0;

    for (draco::PointIndex i(0); i < h->mesh->num_points(); ++i) {
        const uint8_t* src = attr->GetAddress(attr->mapped_index(i));
        if (!attr->IsAddressValid(src) || !attr->IsAddressValid(src + bytes_per_point - 1)) return 0;
        std::memcpy(out + static_cast<int64_t>(i.value()) * bytes_per_point, src, bytes_per_point);
    }
    return 1;
}

DRACO_NATIVE_EXPORT
int32_t draco_mesh_copy_indices_uint32(
    const DracoMeshHandle* h,
    uint32_t* out,
    int32_t out_count) {
    if (!h || !h->mesh || !out) return 0;
    auto face_count_raw = h->mesh->num_faces();
    if (face_count_raw > static_cast<uint32_t>(std::numeric_limits<int32_t>::max())) return 0;
    int32_t face_count = static_cast<int32_t>(face_count_raw);
    int64_t required = static_cast<int64_t>(face_count) * 3;
    if (required <= 0 || required > std::numeric_limits<int32_t>::max()) return 0;
    if (static_cast<int64_t>(out_count) < required) return 0;
    for (draco::FaceIndex f(0); f < face_count; ++f) {
        const auto& face = h->mesh->face(f);
        out[f.value() * 3 + 0] = face[0].value();
        out[f.value() * 3 + 1] = face[1].value();
        out[f.value() * 3 + 2] = face[2].value();
    }
    return 1;
}

DRACO_NATIVE_EXPORT
void draco_mesh_destroy(DracoMeshHandle* h) {
    delete h;
}

}  // extern "C"
