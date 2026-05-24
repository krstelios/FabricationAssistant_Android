cmake_minimum_required(VERSION 3.18)
project(draco_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(DRACO_ROOT ${CMAKE_CURRENT_LIST_DIR}/../../../deps/draco)
if(NOT EXISTS "${DRACO_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR "Draco source not found at ${DRACO_ROOT}. Run `git clone --depth=1 --branch 1.5.7 https://github.com/google/draco.git Android/deps/draco` (Plan 2B Task 1).")
endif()

# Build Draco as a static library and link it into our shared wrapper.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(DRACO_GLTF_BITSTREAM ON CACHE BOOL "" FORCE)
set(DRACO_TESTS OFF CACHE BOOL "" FORCE)
set(DRACO_JS_GLUE OFF CACHE BOOL "" FORCE)
add_subdirectory(${DRACO_ROOT} ${CMAKE_BINARY_DIR}/draco_build EXCLUDE_FROM_ALL)

add_library(draco_native SHARED draco_native.cpp)
target_include_directories(draco_native PRIVATE
    ${DRACO_ROOT}/src
    ${CMAKE_BINARY_DIR}                # Draco generates draco/draco_features.h here
    ${CMAKE_BINARY_DIR}/draco_build)
target_link_libraries(draco_native PRIVATE draco::draco android log)
target_compile_options(draco_native PRIVATE -fvisibility=hidden -ffunction-sections -fdata-sections)
target_link_options(draco_native PRIVATE -Wl,--gc-sections -Wl,--strip-all)
