cmake_minimum_required(VERSION 3.16)
project(SpatialNative7Dof LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT EXISTS "${STAGED_NATIVE}/main.cpp" OR NOT EXISTS "${ORIGINAL_NATIVE}/Eigen/Dense")
    message(FATAL_ERROR "Run build-native.ps1 to stage the native source overlay")
endif()
add_library(spr7_model STATIC
    "${STAGED_NATIVE}/DockingDynamics.cpp"
    "${STAGED_NATIVE}/Parameters.cpp" "${STAGED_NATIVE}/Controller.cpp"
    "${STAGED_NATIVE}/ExternalContactDemo.cpp" "${STAGED_NATIVE}/ExternalPlumeDemo.cpp"
    "${STAGED_NATIVE}/ExternalSeparationDemo.cpp")
target_include_directories(spr7_model PUBLIC "${STAGED_NATIVE}" "${ORIGINAL_NATIVE}" "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_definitions(spr7_model PUBLIC EIGEN_UNROLLING_LIMIT=0 EIGEN_DONT_PARALLELIZE)
add_executable(spr7_cli "${STAGED_NATIVE}/main.cpp" "${STAGED_NATIVE}/DockingCAPI.cpp")
target_link_libraries(spr7_cli PRIVATE spr7_model)
add_library(SpatialNative7Dof SHARED Spr7Api.cpp)
target_link_libraries(SpatialNative7Dof PRIVATE spr7_model)
if(MINGW)
    target_link_options(SpatialNative7Dof PRIVATE -static-libgcc -static-libstdc++ -static)
endif()
foreach(target IN ITEMS spr7_model spr7_cli SpatialNative7Dof)
    set_property(TARGET ${target} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    if(MSVC)
        target_compile_options(${target} PRIVATE /bigobj /utf-8)
    endif()
endforeach()

