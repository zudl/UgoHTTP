cmake_minimum_required(VERSION 3.25)
project(ugo C)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
include("${CMAKE_CURRENT_LIST_DIR}/deps/client_config.cmake")

set(UGO_DEPS "" CACHE PATH "Prefix with the pinned curl, nghttp2, mbedTLS and zlib (include/, lib/)")
set(UGO_CA_HEADER_DIR "" CACHE PATH "Directory holding the generated ca_bundle.h")
option(UGO_SHARED "Build the shared library" ON)
option(UGO_TESTS "Build the native tests against a static copy of the shim" OFF)
option(UGO_DEV_TLS "Accept UGO_CLIENT_TLS_INSECURE_DEV" OFF)

set(UGO_EXPORTS
    ugo_monotonic_us ugo_client_create ugo_client_shutdown ugo_client_destroy ugo_shutdown_all
    ugo_submit ugo_abort ugo_release ugo_get_result ugo_wait_events
    ugo_ws_open ugo_ws_send ugo_ws_close ugo_ws_release)

set(sources src/ugo.c src/platform/posix.c src/platform/windows.c src/platform/windows_tls.c)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    enable_language(OBJC)
    list(APPEND sources src/platform/ios.m)
    set_source_files_properties(src/platform/ios.m PROPERTIES COMPILE_OPTIONS -fobjc-arc)
endif()
set(dep_libs
    "${UGO_DEPS}/lib/libcurl.a" "${UGO_DEPS}/lib/libnghttp2.a" "${UGO_DEPS}/lib/libmbedtls.a"
    "${UGO_DEPS}/lib/libmbedx509.a" "${UGO_DEPS}/lib/libtfpsacrypto.a" "${UGO_DEPS}/lib/libz.a")
set(warnings -Wall -Wextra -Werror)

function(shim_target target)
    target_compile_definitions(${target} PRIVATE _FILE_OFFSET_BITS=64)
    target_include_directories(${target} PRIVATE
        "${CMAKE_CURRENT_LIST_DIR}/include" "${CMAKE_CURRENT_LIST_DIR}/src"
        "${UGO_DEPS}/include" "${UGO_CA_HEADER_DIR}")
    target_compile_options(${target} PRIVATE ${warnings} -fvisibility=hidden -ffunction-sections -fdata-sections)
    if(UGO_DEV_TLS)
        target_compile_definitions(${target} PRIVATE UGO_DEV_TLS)
    endif()
endfunction()

function(link_dependencies target)
    target_link_libraries(${target} PRIVATE ${dep_libs})
    if(APPLE)
        target_link_libraries(${target} PRIVATE "-framework CoreFoundation" "-framework SystemConfiguration")
        if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
            target_link_libraries(${target} PRIVATE "-framework UIKit")
        endif()
    elseif(WIN32)
        target_link_libraries(${target} PRIVATE ws2_32 bcrypt iphlpapi api-ms-win-core-realtime-l1-1-1)
    else()
        if(NOT ANDROID)
            find_package(Threads REQUIRED)
            target_link_libraries(${target} PRIVATE Threads::Threads)
        endif()
        target_link_libraries(${target} PRIVATE m)
    endif()
endfunction()

if(UGO_SHARED AND CMAKE_SYSTEM_NAME STREQUAL "iOS")
    # One relocatable object holding the shim and every dependency, with only ugo_* left global and local
    # symbols stripped: the app links it next to other SDKs that may carry their own curl or mbedTLS.
    add_library(ugo_objects OBJECT ${sources})
    shim_target(ugo_objects)
    target_compile_definitions(ugo_objects PRIVATE UGO_BUILD_SHARED)
    list(TRANSFORM UGO_EXPORTS PREPEND "_" OUTPUT_VARIABLE apple_exports)
    list(JOIN apple_exports "\n" apple_exports)
    file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/exports.txt" "${apple_exports}\n")
    set(prelinked "${CMAKE_CURRENT_BINARY_DIR}/libugo.o")
    set(archive "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/libugo.a")
    add_custom_command(OUTPUT "${archive}"
        COMMAND "${CMAKE_C_COMPILER}" -r -nostdlib -arch ${CMAKE_OSX_ARCHITECTURES} -isysroot "${CMAKE_OSX_SYSROOT}"
            "${CMAKE_C_OSX_DEPLOYMENT_TARGET_FLAG}${CMAKE_OSX_DEPLOYMENT_TARGET}"
            "-Wl,-exported_symbols_list,${CMAKE_CURRENT_BINARY_DIR}/exports.txt" -Wl,-x
            -o "${prelinked}" $<TARGET_OBJECTS:ugo_objects> ${dep_libs}
        COMMAND "${CMAKE_COMMAND}" -E rm -f "${archive}"
        COMMAND xcrun libtool -static -D -no_warning_for_no_symbols -o "${archive}" "${prelinked}"
        DEPENDS ugo_objects $<TARGET_OBJECTS:ugo_objects> ${dep_libs} "${CMAKE_CURRENT_BINARY_DIR}/exports.txt"
        COMMAND_EXPAND_LISTS VERBATIM)
    add_custom_target(ugo ALL DEPENDS "${archive}")
elseif(UGO_SHARED)
    add_library(ugo SHARED ${sources})
    shim_target(ugo)
    target_compile_definitions(ugo PRIVATE UGO_BUILD_SHARED)
    link_dependencies(ugo)
    if(APPLE)
        list(TRANSFORM UGO_EXPORTS PREPEND "_" OUTPUT_VARIABLE apple_exports)
        list(JOIN apple_exports "\n" apple_exports)
        file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/exports.txt" "${apple_exports}\n")
        set(apple_link -Wl,-dead_strip -Wl,-dead_strip_dylibs -Wl,-x
            "-Wl,-exported_symbols_list,${CMAKE_CURRENT_BINARY_DIR}/exports.txt")
        target_link_options(ugo PRIVATE ${apple_link})
        # The Unity plugin is a loadable bundle (MH_BUNDLE); the dylib serves the tests and plain .NET.
        add_library(ugo_bundle MODULE ${sources})
        shim_target(ugo_bundle)
        target_compile_definitions(ugo_bundle PRIVATE UGO_BUILD_SHARED)
        link_dependencies(ugo_bundle)
        target_link_options(ugo_bundle PRIVATE ${apple_link})
        set_target_properties(ugo_bundle PROPERTIES OUTPUT_NAME ugo PREFIX "" SUFFIX "")
    elseif(WIN32)
        # [DllImport("ugo")] looks for ugo.dll; mingw would name it libugo.dll.
        set_target_properties(ugo PROPERTIES PREFIX "" RUNTIME_OUTPUT_DIRECTORY "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}")
        target_link_options(ugo PRIVATE -Wl,--gc-sections -s)
    else()
        list(JOIN UGO_EXPORTS ";\n    " elf_exports)
        file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/ugo.map" "{\n  global:\n    ${elf_exports};\n  local: *;\n};\n")
        target_link_options(ugo PRIVATE -Wl,--gc-sections -Wl,-s -Wl,--no-undefined
            "-Wl,--version-script=${CMAKE_CURRENT_BINARY_DIR}/ugo.map")
        if(ANDROID)
            target_link_options(ugo PRIVATE -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384)
        endif()
    endif()
endif()

if(UGO_TESTS)
    add_library(ugo_core STATIC ${sources})
    shim_target(ugo_core)
    add_library(ugo_core_wrap STATIC ${sources})
    shim_target(ugo_core_wrap)
    target_compile_definitions(ugo_core PRIVATE UGO_TEST_COUNTERS)
    target_compile_definitions(ugo_core_wrap PRIVATE UGO_GENERATION_MAX=3 UGO_TEST_COUNTERS)
    foreach(pair "ugo-tests;ugo_core" "ugo-tests-wrap;ugo_core_wrap")
        list(GET pair 0 exe)
        list(GET pair 1 core)
        add_executable(${exe} tests/test_ugo.c)
        target_include_directories(${exe} PRIVATE "${CMAKE_CURRENT_LIST_DIR}/include" "${UGO_DEPS}/include")
        target_compile_options(${exe} PRIVATE ${warnings})
        target_link_libraries(${exe} PRIVATE ${core})
        link_dependencies(${exe})
    endforeach()
    target_compile_definitions(ugo-tests-wrap PRIVATE UGO_TEST_WRAP_ONLY)
    find_package(Threads REQUIRED)
    target_link_libraries(ugo-tests PRIVATE Threads::Threads)
    target_link_libraries(ugo-tests-wrap PRIVATE Threads::Threads)
endif()
