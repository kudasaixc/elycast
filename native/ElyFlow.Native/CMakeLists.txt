cmake_minimum_required(VERSION 3.20)

project(ElyFlow.Native LANGUAGES CXX)

add_library(ElyFlow.Native SHARED
    src/ElyFlowNative.cpp
    src/ElyFlowRenderer.cpp
    src/NvidiaFrucAdapter.cpp
    src/NvidiaRuntimeLoader.cpp
    src/SystemMediaTransport.cpp
)

target_compile_features(ElyFlow.Native PRIVATE cxx_std_20)
target_compile_definitions(ElyFlow.Native PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX ELYFLOW_NATIVE_EXPORTS)
target_include_directories(ElyFlow.Native PUBLIC include)
target_link_libraries(ElyFlow.Native PRIVATE d3d11 d3dcompiler dxgi opengl32 user32 gdi32 avrt runtimeobject shell32 ole32 propsys)

set(NVOF_SDK_ROOT "${CMAKE_CURRENT_LIST_DIR}/../NVIDIA-Optical-Flow-SDK-5.0.7/Optical_Flow_SDK_5.0.7" CACHE PATH "NVIDIA Optical Flow SDK root")
find_path(NVOFFRUC_INCLUDE_DIR
    NAMES NvOFFRUC.h
    HINTS
        "${NVOF_SDK_ROOT}/NvOFFRUC/Interface"
        "${CMAKE_CURRENT_LIST_DIR}/../Optical_Flow_SDK_5.0.7/NvOFFRUC/Interface"
        "${CMAKE_CURRENT_LIST_DIR}/../../Optical_Flow_SDK_5.0.7/NvOFFRUC/Interface"
    PATH_SUFFIXES NvOFFRUC/Interface Interface
)

if (NVOFFRUC_INCLUDE_DIR)
    target_compile_definitions(ElyFlow.Native PRIVATE ELYFLOW_HAS_NVOFFRUC_SDK=1)
    target_include_directories(ElyFlow.Native PRIVATE "${NVOFFRUC_INCLUDE_DIR}")
    message(STATUS "ElyFlow.Native: NVIDIA NvOFFRUC SDK headers found at ${NVOFFRUC_INCLUDE_DIR}")
else()
    target_compile_definitions(ElyFlow.Native PRIVATE ELYFLOW_HAS_NVOFFRUC_SDK=0)
    message(STATUS "ElyFlow.Native: NVIDIA NvOFFRUC SDK headers not found; adapter will compile as unavailable.")
endif()

if (MSVC)
    target_compile_options(ElyFlow.Native PRIVATE /W4 /permissive- /EHsc)
endif()

set_target_properties(ElyFlow.Native PROPERTIES
    OUTPUT_NAME "ElyFlow.Native"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_CURRENT_SOURCE_DIR}/bin/Debug"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/bin/Release"
)
