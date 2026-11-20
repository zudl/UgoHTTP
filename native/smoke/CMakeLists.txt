cmake_minimum_required(VERSION 3.25)
project(ugo_smoke C)
set(CMAKE_C_STANDARD 11)
include("${CMAKE_CURRENT_LIST_DIR}/../deps/client_config.cmake")
add_executable(ugo-smoke smoke.c)
target_include_directories(ugo-smoke PRIVATE "${UGO_OUTPUT}/include" "${CMAKE_CURRENT_BINARY_DIR}")
target_compile_definitions(ugo-smoke PRIVATE CURL_STATICLIB)
target_compile_options(ugo-smoke PRIVATE -Wall -Wextra -Werror -fvisibility=hidden)
target_link_libraries(ugo-smoke PRIVATE
    "${UGO_OUTPUT}/lib/libcurl.a"
    "${UGO_OUTPUT}/lib/libnghttp2.a"
    "${UGO_OUTPUT}/lib/libmbedtls.a"
    "${UGO_OUTPUT}/lib/libmbedx509.a"
    "${UGO_OUTPUT}/lib/libtfpsacrypto.a"
    "${UGO_OUTPUT}/lib/libz.a")
if(NOT ANDROID)
    find_package(Threads REQUIRED)
    target_link_libraries(ugo-smoke PRIVATE Threads::Threads)
endif()
if(APPLE)
    target_link_libraries(ugo-smoke PRIVATE "-framework Security" "-framework CoreFoundation" "-framework SystemConfiguration")
elseif(WIN32)
    target_sources(ugo-smoke PRIVATE ../src/platform/windows_tls.c)
    target_link_libraries(ugo-smoke PRIVATE ws2_32 bcrypt iphlpapi)
    add_executable(ugo-dll-probe dll_probe.c)
    target_include_directories(ugo-dll-probe PRIVATE "${CMAKE_CURRENT_LIST_DIR}/../include")
    target_compile_options(ugo-dll-probe PRIVATE -Wall -Wextra -Werror)
    target_link_libraries(ugo-dll-probe PRIVATE ws2_32)
else()
    target_link_libraries(ugo-smoke PRIVATE m)
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    add_executable(ugo-concurrent-tls concurrent_tls.c)
    target_include_directories(ugo-concurrent-tls PRIVATE "${UGO_OUTPUT}/include")
    target_compile_options(ugo-concurrent-tls PRIVATE -Wall -Wextra -Werror -fvisibility=hidden)
    target_link_libraries(ugo-concurrent-tls PRIVATE
        "${UGO_OUTPUT}/lib/libcurl.a" "${UGO_OUTPUT}/lib/libnghttp2.a"
        "${UGO_OUTPUT}/lib/libmbedtls.a" "${UGO_OUTPUT}/lib/libmbedx509.a"
        "${UGO_OUTPUT}/lib/libtfpsacrypto.a" "${UGO_OUTPUT}/lib/libz.a" Threads::Threads m)
endif()
