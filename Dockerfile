# ---- build libOpenCvSharpExtern.so from source ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS native-build

ARG OCVSHARP_TAG=4.13.0.20260602

RUN apt-get update && apt-get install -y --no-install-recommends \
    cmake git build-essential \
    libopencv-dev \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --depth 1 --branch ${OCVSHARP_TAG} \
    https://github.com/shimat/opencvsharp.git /opencvsharp

# Ubuntu's apt OpenCV (4.6) is missing APIs used by these wrapper modules:
#   xfeatures2d  — patented SIFT/SURF, excluded from apt builds
#   barcode      — cv::barcode::BarcodeDetector added in OpenCV 4.8+
#   aruco        — ArucoDetector/CharucoDetector/RefineParameters added in OpenCV 4.7+
# None of these are used by DoorWatch, so remove them before building.
RUN sed -i '/#include <opencv2\/xfeatures2d.hpp>/d' \
        /opencvsharp/src/OpenCvSharpExtern/include_opencv.h \
    && rm -f /opencvsharp/src/OpenCvSharpExtern/xfeatures2d.cpp \
             /opencvsharp/src/OpenCvSharpExtern/barcode.cpp \
             /opencvsharp/src/OpenCvSharpExtern/aruco.cpp

RUN cmake -S /opencvsharp/src/OpenCvSharpExtern \
          -B /opencvsharp/build \
          -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /opencvsharp/build --parallel $(nproc)

RUN test -f /opencvsharp/build/libOpenCvSharpExtern.so \
    || (echo "ERROR: libOpenCvSharpExtern.so was not built" && exit 1)


# ---- build .NET app ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props .
COPY ["src/DoorWatch.Worker/DoorWatch.Worker.csproj", "src/DoorWatch.Worker/"]
COPY ["src/DoorWatch.Core/DoorWatch.Core.csproj", "src/DoorWatch.Core/"]
COPY ["src/DoorWatch.Camera/DoorWatch.Camera.csproj", "src/DoorWatch.Camera/"]
COPY ["src/DoorWatch.HomeAssistant/DoorWatch.HomeAssistant.csproj", "src/DoorWatch.HomeAssistant/"]
RUN dotnet restore "src/DoorWatch.Worker/DoorWatch.Worker.csproj"

COPY . .
RUN dotnet publish "src/DoorWatch.Worker/DoorWatch.Worker.csproj" \
    -c Release -o /app/publish --no-restore


# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    libopencv-dev \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY --from=native-build /opencvsharp/build/libOpenCvSharpExtern.so /app/

# Tell the OS dynamic linker to search /app so dlopen finds libOpenCvSharpExtern.so.
ENV LD_LIBRARY_PATH=/app

VOLUME ["/data"]

# Diagnostics HTTP endpoints (/status, /healthz). Mapped to the host in docker-compose.
EXPOSE 8080

ENTRYPOINT ["dotnet", "DoorWatch.Worker.dll"]
