# ---- build libOpenCvSharpExtern.so from source ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS native-build

ARG OCVSHARP_TAG=4.6.0.20221108

RUN apt-get update && apt-get install -y --no-install-recommends \
    cmake git build-essential \
    libopencv-dev \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --depth 1 --branch ${OCVSHARP_TAG} \
    https://github.com/shimat/opencvsharp.git /opencvsharp

# Ubuntu's OpenCV apt packages exclude xfeatures2d (patented SIFT/SURF algorithms).
# We don't use any xfeatures2d functions, so remove the include and its source file.
RUN sed -i '/#include <opencv2\/xfeatures2d.hpp>/d' \
        /opencvsharp/src/OpenCvSharpExtern/include_opencv.h \
    && rm -f /opencvsharp/src/OpenCvSharpExtern/xfeatures2d.cpp

RUN cmake -S /opencvsharp/src/OpenCvSharpExtern \
          -B /opencvsharp/build \
          -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /opencvsharp/build --parallel $(nproc)

RUN test -f /opencvsharp/build/libOpenCvSharpExtern.so \
    || (echo "ERROR: libOpenCvSharpExtern.so was not built" && exit 1)


# ---- build .NET app ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

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

COPY --from=native-build /opencvsharp/build/libOpenCvSharpExtern.so /usr/local/lib/
RUN ldconfig

COPY --from=build /app/publish .

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "DoorWatch.Worker.dll"]
