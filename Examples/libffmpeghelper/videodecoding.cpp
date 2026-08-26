#include "stdafx.h"
#include <limits>

struct VideoDecoderContext
{
	const AVCodec *codec;
	AVCodecContext *av_codec_context;
	AVFrame *frame;
	AVFrame *software_frame;
	AVFrame *active_frame;
	AVBufferRef *hw_device_ctx;
	AVPixelFormat hw_pixel_format;
	uint8_t *input_buffer;
	unsigned int input_buffer_size;
	bool hardware_enabled;
	bool prefer_hardware;
	HWND render_hwnd;
	IDXGISwapChain *swap_chain;
	ID3D11RenderTargetView *render_target_view;
	ID3D11VertexShader *vertex_shader;
	ID3D11PixelShader *pixel_shader;
	ID3D11SamplerState *sampler_state;
	ID3D11Buffer *crop_buffer;
	ID3D11Texture2D *shader_texture;
	ID3D11ShaderResourceView *shader_resource_view_y;
	ID3D11ShaderResourceView *shader_resource_view_uv;
	DXGI_FORMAT shader_texture_format;
	int shader_texture_width;
	int shader_texture_height;
	int render_width;
	int render_height;
};

struct RenderCropConstants
{
	float crop_left;
	float crop_top;
	float crop_right;
	float crop_bottom;
};

struct ScalerContext
{
	SwsContext *sws_context;

	int source_left;
	int source_top;
	int source_height;
	AVPixelFormat source_pixel_format;
	int scaled_width;
	int scaled_height;
	AVPixelFormat scaled_pixel_format;
};

static int prepare_padded_decoder_packet(VideoDecoderContext *context, void *rawBuffer,
	int rawBufferLength, AVPacket *packet)
{
	if (!context || !rawBuffer || rawBufferLength <= 0 || !packet)
		return -1;

	const size_t paddedLength = static_cast<size_t>(rawBufferLength) + AV_INPUT_BUFFER_PADDING_SIZE;
	if (paddedLength > (std::numeric_limits<unsigned int>::max)())
		return -2;

	av_fast_malloc(&context->input_buffer, &context->input_buffer_size, paddedLength);
	if (!context->input_buffer)
		return -3;

	memcpy(context->input_buffer, rawBuffer, static_cast<size_t>(rawBufferLength));
	memset(context->input_buffer + rawBufferLength, 0, AV_INPUT_BUFFER_PADDING_SIZE);

	av_init_packet(packet);
	packet->data = context->input_buffer;
	packet->size = rawBufferLength;
	return 0;
}

static void release_render_resources(VideoDecoderContext *context)
{
	if (!context)
		return;

	if (context->render_target_view)
	{
		context->render_target_view->Release();
		context->render_target_view = nullptr;
	}

	if (context->swap_chain)
	{
		context->swap_chain->Release();
		context->swap_chain = nullptr;
	}

	if (context->vertex_shader)
	{
		context->vertex_shader->Release();
		context->vertex_shader = nullptr;
	}

	if (context->pixel_shader)
	{
		context->pixel_shader->Release();
		context->pixel_shader = nullptr;
	}

	if (context->sampler_state)
	{
		context->sampler_state->Release();
		context->sampler_state = nullptr;
	}

	if (context->crop_buffer)
	{
		context->crop_buffer->Release();
		context->crop_buffer = nullptr;
	}

	if (context->shader_texture)
	{
		context->shader_texture->Release();
		context->shader_texture = nullptr;
	}

	if (context->shader_resource_view_y)
	{
		context->shader_resource_view_y->Release();
		context->shader_resource_view_y = nullptr;
	}

	if (context->shader_resource_view_uv)
	{
		context->shader_resource_view_uv->Release();
		context->shader_resource_view_uv = nullptr;
	}

	context->shader_texture_format = DXGI_FORMAT_UNKNOWN;
	context->shader_texture_width = 0;
	context->shader_texture_height = 0;
	context->render_width = 0;
	context->render_height = 0;
}

static int compile_shader(const char *source, const char *entryPoint, const char *target, ID3DBlob **blob)
{
	ID3DBlob *errors = nullptr;
	const HRESULT hr = D3DCompile(source, strlen(source), nullptr, nullptr, nullptr,
		entryPoint, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, blob, &errors);

	if (errors)
		errors->Release();

	return SUCCEEDED(hr) ? 0 : -1;
}

static int ensure_render_shaders(VideoDecoderContext *context, ID3D11Device *device)
{
	if (context->vertex_shader && context->pixel_shader && context->sampler_state && context->crop_buffer)
		return 0;

	const char *vertexShaderSource =
		"cbuffer CropBuffer : register(b0) { float4 crop; };"
		"struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };"
		"VSOut main(uint id : SV_VertexID) {"
		"  float2 pos[3] = { float2(-1.0,-1.0), float2(-1.0,3.0), float2(3.0,-1.0) };"
		"  float2 uv[3] = { float2(0.0,1.0), float2(0.0,-1.0), float2(2.0,1.0) };"
		"  VSOut output;"
		"  output.pos = float4(pos[id], 0.0, 1.0);"
		"  output.uv = float2(lerp(crop.x, 1.0 - crop.z, uv[id].x), lerp(crop.y, 1.0 - crop.w, uv[id].y));"
		"  return output;"
		"}";

	const char *pixelShaderSource =
		"Texture2D texY : register(t0);"
		"Texture2D texUV : register(t1);"
		"SamplerState samp : register(s0);"
		"struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };"
		"float4 main(VSOut input) : SV_TARGET {"
		"  float y = texY.Sample(samp, input.uv).r;"
		"  float2 uv = texUV.Sample(samp, input.uv).rg - float2(0.5, 0.5);"
		"  float r = y + 1.5748 * uv.y;"
		"  float g = y - 0.1873 * uv.x - 0.4681 * uv.y;"
		"  float b = y + 1.8556 * uv.x;"
		"  return float4(saturate(float3(r, g, b)), 1.0);"
		"}";

	ID3DBlob *vertexBlob = nullptr;
	ID3DBlob *pixelBlob = nullptr;

	if (compile_shader(vertexShaderSource, "main", "vs_4_0", &vertexBlob) != 0)
		return -1;

	if (compile_shader(pixelShaderSource, "main", "ps_4_0", &pixelBlob) != 0)
	{
		vertexBlob->Release();
		return -2;
	}

	HRESULT hr = device->CreateVertexShader(vertexBlob->GetBufferPointer(), vertexBlob->GetBufferSize(),
		nullptr, &context->vertex_shader);
	vertexBlob->Release();
	if (FAILED(hr))
	{
		pixelBlob->Release();
		return -3;
	}

	hr = device->CreatePixelShader(pixelBlob->GetBufferPointer(), pixelBlob->GetBufferSize(),
		nullptr, &context->pixel_shader);
	pixelBlob->Release();
	if (FAILED(hr))
		return -4;

	D3D11_SAMPLER_DESC samplerDesc = {};
	samplerDesc.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
	samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
	samplerDesc.MinLOD = 0;
	samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;

	hr = device->CreateSamplerState(&samplerDesc, &context->sampler_state);
	if (FAILED(hr))
		return -5;

	D3D11_BUFFER_DESC bufferDesc = {};
	bufferDesc.ByteWidth = sizeof(RenderCropConstants);
	bufferDesc.Usage = D3D11_USAGE_DYNAMIC;
	bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
	bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

	hr = device->CreateBuffer(&bufferDesc, nullptr, &context->crop_buffer);
	return SUCCEEDED(hr) ? 0 : -6;
}

static int ensure_swap_chain(VideoDecoderContext *context, ID3D11Device *device)
{
	if (!context->render_hwnd)
		return -1;

	RECT rect = {};
	GetClientRect(context->render_hwnd, &rect);
	const int width = max(1, rect.right - rect.left);
	const int height = max(1, rect.bottom - rect.top);

	if (context->swap_chain && context->render_width == width && context->render_height == height)
		return 0;

	if (context->render_target_view)
	{
		context->render_target_view->Release();
		context->render_target_view = nullptr;
	}

	if (!context->swap_chain)
	{
		IDXGIDevice *dxgiDevice = nullptr;
		IDXGIAdapter *adapter = nullptr;
		IDXGIFactory *factory = nullptr;

		if (FAILED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void **>(&dxgiDevice))))
			return -2;

		if (FAILED(dxgiDevice->GetAdapter(&adapter)))
		{
			dxgiDevice->Release();
			return -3;
		}

		if (FAILED(adapter->GetParent(__uuidof(IDXGIFactory), reinterpret_cast<void **>(&factory))))
		{
			adapter->Release();
			dxgiDevice->Release();
			return -4;
		}

		DXGI_SWAP_CHAIN_DESC desc = {};
		desc.BufferCount = 2;
		desc.BufferDesc.Width = width;
		desc.BufferDesc.Height = height;
		desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
		desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		desc.OutputWindow = context->render_hwnd;
		desc.SampleDesc.Count = 1;
		desc.Windowed = TRUE;
		desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

		const HRESULT hr = factory->CreateSwapChain(device, &desc, &context->swap_chain);

		factory->Release();
		adapter->Release();
		dxgiDevice->Release();

		if (FAILED(hr))
			return -5;
	}
	else
	{
		if (FAILED(context->swap_chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0)))
			return -6;
	}

	ID3D11Texture2D *backBuffer = nullptr;
	if (FAILED(context->swap_chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void **>(&backBuffer))))
		return -7;

	const HRESULT hr = device->CreateRenderTargetView(backBuffer, nullptr, &context->render_target_view);
	backBuffer->Release();

	if (FAILED(hr))
		return -8;

	context->render_width = width;
	context->render_height = height;
	return 0;
}

static int ensure_shader_texture(VideoDecoderContext *context, ID3D11Device *device, const D3D11_TEXTURE2D_DESC &sourceDesc)
{
	if (context->shader_texture &&
		context->shader_texture_width == static_cast<int>(sourceDesc.Width) &&
		context->shader_texture_height == static_cast<int>(sourceDesc.Height) &&
		context->shader_texture_format == sourceDesc.Format &&
		context->shader_resource_view_y &&
		context->shader_resource_view_uv)
	{
		return 0;
	}

	if (context->shader_resource_view_y)
	{
		context->shader_resource_view_y->Release();
		context->shader_resource_view_y = nullptr;
	}

	if (context->shader_resource_view_uv)
	{
		context->shader_resource_view_uv->Release();
		context->shader_resource_view_uv = nullptr;
	}

	if (context->shader_texture)
	{
		context->shader_texture->Release();
		context->shader_texture = nullptr;
	}

	D3D11_TEXTURE2D_DESC desc = {};
	desc.Width = sourceDesc.Width;
	desc.Height = sourceDesc.Height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = sourceDesc.Format;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

	const HRESULT hr = device->CreateTexture2D(&desc, nullptr, &context->shader_texture);

	if (FAILED(hr))
		return -1;

	DXGI_FORMAT yFormat = DXGI_FORMAT_UNKNOWN;
	DXGI_FORMAT uvFormat = DXGI_FORMAT_UNKNOWN;

	if (sourceDesc.Format == DXGI_FORMAT_NV12)
	{
		yFormat = DXGI_FORMAT_R8_UNORM;
		uvFormat = DXGI_FORMAT_R8G8_UNORM;
	}
	else if (sourceDesc.Format == DXGI_FORMAT_P010 || sourceDesc.Format == DXGI_FORMAT_P016)
	{
		yFormat = DXGI_FORMAT_R16_UNORM;
		uvFormat = DXGI_FORMAT_R16G16_UNORM;
	}
	else
	{
		return -2;
	}

	D3D11_SHADER_RESOURCE_VIEW_DESC yDesc = {};
	yDesc.Format = yFormat;
	yDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
	yDesc.Texture2D.MostDetailedMip = 0;
	yDesc.Texture2D.MipLevels = 1;

	D3D11_SHADER_RESOURCE_VIEW_DESC uvDesc = yDesc;
	uvDesc.Format = uvFormat;

	if (FAILED(device->CreateShaderResourceView(context->shader_texture, &yDesc, &context->shader_resource_view_y)))
		return -3;

	if (FAILED(device->CreateShaderResourceView(context->shader_texture, &uvDesc, &context->shader_resource_view_uv)))
		return -4;

	context->shader_texture_width = static_cast<int>(sourceDesc.Width);
	context->shader_texture_height = static_cast<int>(sourceDesc.Height);
	context->shader_texture_format = sourceDesc.Format;
	return 0;
}

//struct DrawContext
//{
//	ID3D11Device* d3d_device;
//	ID3D11DeviceContext* d3d_context;
//	ID2D1DeviceContext* d2d1_context;
//	ID2D1Device* d2d1_device;
////	IDXGISwapChain* swap_chain;
//////	ID3D11RenderTargetView* target_view;
//	HWND m_hwnd;
//	ID2D1Factory1* m_pD2DFactory;
//	ID2D1HwndRenderTarget* m_pRenderTarget;
////	ID2D1Bitmap* m_pBitmap;
//};
//
//int set_window_hwnd(void* handle, void** contextHandle)
//{
//	if (!handle)
//		return -1;
//
//	auto context = static_cast<DrawContext*>(av_mallocz(sizeof(DrawContext)));
//
//	if (!context)
//		return -2;
//
//	//DXGI_SWAP_CHAIN_DESC desc = {};
//	//desc.BufferCount = 1;
//	//desc.BufferDesc.Width = 640;
//	//desc.BufferDesc.Height = 480;
//	//desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
//	//desc.OutputWindow = (HWND)handle;
//	//desc.SampleDesc.Count = 1;
//	//desc.Windowed = TRUE;
//	//desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
//
//	//D3D11CreateDeviceAndSwapChain(
//	//	nullptr,
//	//	D3D_DRIVER_TYPE_HARDWARE,
//	//	nullptr,
//	//	0,
//	//	nullptr,
//	//	0,
//	//	D3D11_SDK_VERSION,
//	//	&desc,
//	//	&context->swap_chain,
//	//	&context->d3d_device,
//	//	nullptr,
//	//	&context->d3d_context);
//
//
//	//ID3D11Texture2D* back_buffer = nullptr;
//	//context->swap_chain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back_buffer);
//	//context->d3d_device->CreateRenderTargetView(back_buffer, nullptr, &context->target_view);
//	//back_buffer->Release();
//
//	//context->d3d_context->OMSetRenderTargets(1, &context->target_view, nullptr);
//
//	*contextHandle = context;
//
//	D3D11CreateDevice(
//		nullptr,
//		D3D_DRIVER_TYPE_HARDWARE,
//		nullptr,
//		0,
//		nullptr,
//		0,
//		D3D11_SDK_VERSION,
//		&context->d3d_device,
//		nullptr,
//		&context->d3d_context);
//
//	RECT rc;
//	GetClientRect((HWND)handle, &rc);
//
//	D2D1_SIZE_U size = D2D1::SizeU(rc.right - rc.left, rc.bottom - rc.top);
//
//	if (!context->m_pD2DFactory)
//	{
//		D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &context->m_pD2DFactory);
//	}
//
//	context->m_pD2DFactory->CreateHwndRenderTarget(
//		D2D1::RenderTargetProperties(),
//		D2D1::HwndRenderTargetProperties((HWND)handle, size),
//		&context->m_pRenderTarget);
//
//	IDXGIDevice1* dxgiDevice = nullptr;
//	context->d3d_device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDevice);
//	context->m_pD2DFactory->CreateDevice(dxgiDevice, &context->d2d1_device);
//	dxgiDevice->Release();
//
//	context->d2d1_device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context->d2d1_context);
//
//	//D2D1_RENDER_TARGET_PROPERTIES rtProps = D2D1::RenderTargetProperties(
//	//	D2D1_RENDER_TARGET_TYPE_HARDWARE,
//	//	D2D1::PixelFormat(DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
//	//	0.96f, 0.96f,
//	//	D2D1_RENDER_TARGET_USAGE_GDI_COMPATIBLE);
//
//
//	return 0;
//}

//void render_frame(void* handle, AVFrame *frame)
//{
//	const auto context = static_cast<DrawContext*>(handle);
//
//	ID3D11Texture2D* texture = nullptr;
//
//	D3D11_TEXTURE2D_DESC desc = {};
//	desc.MipLevels = 1;
//	desc.Width = frame->width;
//	desc.Height = frame->height;
//	desc.ArraySize = 1;
//	desc.SampleDesc.Count = 1;
//	desc.Usage = D3D11_USAGE_DYNAMIC;
//	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
//	desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
//	desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
//
//	int ret = context->d3d_device->CreateTexture2D(&desc, nullptr, &texture);
//	if (ret < 0) 
//		return;
//
//	D3D11_MAPPED_SUBRESOURCE mappedResource;
//	ret = context->d3d_context->Map(texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mappedResource);
//	if (ret < 0)
//	{
//		texture->Release();
//		return;
//	}
//
//	memcpy(mappedResource.pData, frame->data[0], frame->width * frame->height * 3);
//
//	context->d3d_context->Unmap(texture, 0);
//
//	IDXGISurface* dxgiSurface;
//	ret = texture->QueryInterface(__uuidof(IDXGISurface), (void**)&dxgiSurface);
//
//	D2D1_BITMAP_PROPERTIES1 bitmapProperties = D2D1::BitmapProperties1(
//		D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
//		D2D1::PixelFormat(DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
//		0.96f, 0.96f);
//
//	ID2D1Bitmap1* bitmap;
//	context->d2d1_context->CreateBitmapFromDxgiSurface(dxgiSurface, &bitmapProperties, &bitmap);
//
//	context->m_pRenderTarget->BeginDraw();
//	context->m_pRenderTarget->Clear(D2D1::ColorF(D2D1::ColorF::AliceBlue));
//
//	D2D1_SIZE_F bitmapSize = bitmap->GetSize();
//
//	context->m_pRenderTarget->DrawBitmap(bitmap, D2D1::RectF(0,0, bitmapSize.width, bitmapSize.height));
//
//	context->m_pRenderTarget->EndDraw();
//
//	dxgiSurface->Release();
//
//
//	//ID3D11ShaderResourceView* shaderResourceView = nullptr;
//	//ret = context->d3d_device->CreateShaderResourceView(texture, nullptr, &shaderResourceView);
//	//if (ret < 0) 
//	//	return;
//
//	//float clearColor[] = { 1.0f, 1.0f, 0.0f, 1.0f };
//	//context->d3d_context->ClearRenderTargetView(context->target_view, clearColor);
//
//	//context->d3d_context->OMSetRenderTargets(1, &context->target_view, nullptr);
//
//	//context->d3d_context->PSSetShaderResources(0, 1, &shaderResourceView);
//
//
//	//context->swap_chain->Present(1, 0);
//	//shaderResourceView->Release();
//	texture->Release();
//}

static AVPixelFormat get_hw_format(AVCodecContext *avctx, const AVPixelFormat *pixel_formats)
{
	const auto context = static_cast<VideoDecoderContext *>(avctx->opaque);

	if (context && context->hardware_enabled)
	{
		for (const AVPixelFormat *format = pixel_formats; *format != AV_PIX_FMT_NONE; format++)
		{
			if (*format == context->hw_pixel_format)
				return *format;
		}
	}

	return avcodec_default_get_format(avctx, pixel_formats);
}

static bool try_enable_d3d11va(VideoDecoderContext *context)
{
	context->hardware_enabled = false;
	context->hw_pixel_format = AV_PIX_FMT_NONE;
	av_buffer_unref(&context->hw_device_ctx);

	for (int i = 0;; i++)
	{
		const AVCodecHWConfig *config = avcodec_get_hw_config(context->codec, i);

		if (!config)
			return false;

		if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) &&
			config->device_type == AV_HWDEVICE_TYPE_D3D11VA)
		{
			context->hw_pixel_format = config->pix_fmt;
			break;
		}
	}

	if (av_hwdevice_ctx_create(&context->hw_device_ctx, AV_HWDEVICE_TYPE_D3D11VA, nullptr, nullptr, 0) < 0)
	{
		context->hw_pixel_format = AV_PIX_FMT_NONE;
		return false;
	}

	context->av_codec_context->hw_device_ctx = av_buffer_ref(context->hw_device_ctx);

	if (!context->av_codec_context->hw_device_ctx)
	{
		av_buffer_unref(&context->hw_device_ctx);
		context->hw_pixel_format = AV_PIX_FMT_NONE;
		return false;
	}

	context->hardware_enabled = true;
	return true;
}

static int open_decoder(VideoDecoderContext *context, bool preferHardware)
{
	context->av_codec_context->opaque = context;
	context->av_codec_context->get_format = get_hw_format;

	if (preferHardware && try_enable_d3d11va(context))
	{
		if (avcodec_open2(context->av_codec_context, context->codec, nullptr) == 0)
			return 0;

		avcodec_close(context->av_codec_context);
		av_buffer_unref(&context->av_codec_context->hw_device_ctx);
		av_buffer_unref(&context->hw_device_ctx);
		context->hardware_enabled = false;
		context->hw_pixel_format = AV_PIX_FMT_NONE;
	}

	context->av_codec_context->get_format = avcodec_default_get_format;
	context->av_codec_context->opaque = nullptr;

	return avcodec_open2(context->av_codec_context, context->codec, nullptr);
}

static void fallback_to_software_decoder(VideoDecoderContext *context)
{
	avcodec_close(context->av_codec_context);
	av_buffer_unref(&context->av_codec_context->hw_device_ctx);
	av_buffer_unref(&context->hw_device_ctx);
	context->hardware_enabled = false;
	context->hw_pixel_format = AV_PIX_FMT_NONE;
	open_decoder(context, false);
}

static int recreate_decoder_with_extradata(VideoDecoderContext *context, void *extradata, int extradataLength)
{
	if (!context || !extradata || extradataLength <= 0)
		return -1;

	AVCodecContext *newCodecContext = avcodec_alloc_context3(context->codec);
	if (!newCodecContext)
		return -2;

	newCodecContext->extradata = static_cast<uint8_t *>(av_malloc(extradataLength + AV_INPUT_BUFFER_PADDING_SIZE));
	if (!newCodecContext->extradata)
	{
		avcodec_free_context(&newCodecContext);
		return -3;
	}

	memcpy(newCodecContext->extradata, extradata, extradataLength);
	memset(newCodecContext->extradata + extradataLength, 0, AV_INPUT_BUFFER_PADDING_SIZE);
	newCodecContext->extradata_size = extradataLength;

	AVCodecContext *oldCodecContext = context->av_codec_context;
	context->av_codec_context = newCodecContext;
	context->hardware_enabled = false;
	context->hw_pixel_format = AV_PIX_FMT_NONE;
	av_buffer_unref(&context->hw_device_ctx);

	if (open_decoder(context, context->prefer_hardware) < 0)
	{
		avcodec_free_context(&context->av_codec_context);
		context->av_codec_context = oldCodecContext;
		open_decoder(context, false);
		return -4;
	}

	avcodec_free_context(&oldCodecContext);
	return 0;
}

int create_video_decoder_with_options(int codec_id, int preferHardwareAcceleration, void **handle)
{
	if (!handle)
		return -1;

	auto context = static_cast<VideoDecoderContext *>(av_mallocz(sizeof(VideoDecoderContext)));

	if (!context)
		return -2;

	context->codec = avcodec_find_decoder(static_cast<AVCodecID>(codec_id));
	if (!context->codec)
	{
		remove_video_decoder(context);
		return -3;
	}

	context->av_codec_context = avcodec_alloc_context3(context->codec);
	if (!context->av_codec_context)
	{
		remove_video_decoder(context);
		return -4;
	}

	if (open_decoder(context, preferHardwareAcceleration != 0) < 0)
	{
		remove_video_decoder(context);
		return -5;
	}
	context->prefer_hardware = preferHardwareAcceleration != 0;

	context->frame = av_frame_alloc();
	if (!context->frame)
	{
		remove_video_decoder(context);
		return -6;
	}

	context->software_frame = av_frame_alloc();
	if (!context->software_frame)
	{
		remove_video_decoder(context);
		return -7;
	}

	*handle = context;

	return 0;
}

int set_video_decoder_extradata(void *handle, void *extradata, int extradataLength)
{
#if _DEBUG
	if (!handle || !extradata || !extradataLength)
		return -1;
#endif

	const auto context = static_cast<VideoDecoderContext *>(handle);

	if (recreate_decoder_with_extradata(context, extradata, extradataLength) < 0)
		return -3;

	return 0;
}

int decode_video_frame(void *handle, void *rawBuffer, int rawBufferLength, int *frameWidth, int *frameHeight, int *framePixelFormat)
{
#if _DEBUG
	if (!handle || !rawBuffer || !rawBufferLength || !frameWidth || !frameHeight || !framePixelFormat)
		return -1;

	if (reinterpret_cast<uintptr_t>(rawBuffer) % 4 != 0)
		return -2;
#endif

	auto context = static_cast<VideoDecoderContext *>(handle);

	av_frame_unref(context->frame);
	av_frame_unref(context->software_frame);
	context->active_frame = nullptr;

	AVPacket packet;
	if (prepare_padded_decoder_packet(context, rawBuffer, rawBufferLength, &packet) < 0)
		return -6;

	int len = avcodec_send_packet(context->av_codec_context, &packet);

	if (len < 0)
		return -3;

	len = avcodec_receive_frame(context->av_codec_context, context->frame);

	if (len < 0)
		return -4;

	context->active_frame = context->frame;

	if (context->frame->format == context->hw_pixel_format)
	{
		if (av_hwframe_transfer_data(context->software_frame, context->frame, 0) < 0)
		{
			fallback_to_software_decoder(context);
			return -5;
		}

		context->active_frame = context->software_frame;
	}

	*frameWidth = context->active_frame->width;
	*frameHeight = context->active_frame->height;
	*framePixelFormat = context->active_frame->format;

	return 0;
}

int set_video_decoder_render_target(void *handle, void *hwnd)
{
	if (!handle)
		return -1;

	auto context = static_cast<VideoDecoderContext *>(handle);

	if (context->render_hwnd != reinterpret_cast<HWND>(hwnd))
	{
		release_render_resources(context);
		context->render_hwnd = reinterpret_cast<HWND>(hwnd);
	}

	return context->render_hwnd ? 0 : -2;
}

int decode_video_frame_to_gpu(void *handle, void *rawBuffer, int rawBufferLength, int *frameWidth, int *frameHeight, int *framePixelFormat)
{
#if _DEBUG
	if (!handle || !rawBuffer || !rawBufferLength || !frameWidth || !frameHeight || !framePixelFormat)
		return -1;
#endif

	auto context = static_cast<VideoDecoderContext *>(handle);

	if (!context->hardware_enabled)
		return -2;

	av_frame_unref(context->frame);
	av_frame_unref(context->software_frame);
	context->active_frame = nullptr;

	AVPacket packet;
	if (prepare_padded_decoder_packet(context, rawBuffer, rawBufferLength, &packet) < 0)
		return -6;

	int len = avcodec_send_packet(context->av_codec_context, &packet);

	if (len < 0)
		return -3;

	len = avcodec_receive_frame(context->av_codec_context, context->frame);

	if (len < 0)
		return -4;

	if (context->frame->format != context->hw_pixel_format)
		return -5;

	context->active_frame = context->frame;
	*frameWidth = context->active_frame->width;
	*frameHeight = context->active_frame->height;
	*framePixelFormat = context->active_frame->format;

	return 0;
}

int render_gpu_decoded_video_frame(void *handle, double cropLeft, double cropTop, double cropRight, double cropBottom)
{
	if (!handle)
		return -1;

	auto context = static_cast<VideoDecoderContext *>(handle);

	if (!context->active_frame || context->active_frame->format != context->hw_pixel_format)
		return -2;

	if (!context->hw_device_ctx || !context->render_hwnd)
		return -3;

	auto deviceContext = reinterpret_cast<AVHWDeviceContext *>(context->hw_device_ctx->data);
	auto d3d11Context = static_cast<AVD3D11VADeviceContext *>(deviceContext->hwctx);

	if (!d3d11Context || !d3d11Context->device || !d3d11Context->device_context)
		return -4;

	ID3D11Device *device = d3d11Context->device;
	ID3D11DeviceContext *immediateContext = d3d11Context->device_context;

	int result = ensure_render_shaders(context, device);
	if (result != 0)
		return -10 + result;

	result = ensure_swap_chain(context, device);
	if (result != 0)
		return -20 + result;

	ID3D11Texture2D *texture = reinterpret_cast<ID3D11Texture2D *>(context->active_frame->data[0]);
	const UINT arraySlice = static_cast<UINT>(reinterpret_cast<uintptr_t>(context->active_frame->data[1]));

	if (!texture)
		return -5;

	D3D11_TEXTURE2D_DESC textureDesc = {};
	texture->GetDesc(&textureDesc);

	if (textureDesc.Format != DXGI_FORMAT_NV12 &&
		textureDesc.Format != DXGI_FORMAT_P010 &&
		textureDesc.Format != DXGI_FORMAT_P016)
		return -8;

	result = ensure_shader_texture(context, device, textureDesc);
	if (result != 0)
		return -30 + result;

	immediateContext->CopySubresourceRegion(context->shader_texture, 0, 0, 0, 0,
		texture, D3D11CalcSubresource(0, arraySlice, 1), nullptr);

	ID3D11ShaderResourceView *views[2] = { context->shader_resource_view_y, context->shader_resource_view_uv };

	D3D11_MAPPED_SUBRESOURCE mapped = {};
	if (SUCCEEDED(immediateContext->Map(context->crop_buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
	{
		auto constants = static_cast<RenderCropConstants *>(mapped.pData);
		constants->crop_left = static_cast<float>(cropLeft);
		constants->crop_top = static_cast<float>(cropTop);
		constants->crop_right = static_cast<float>(cropRight);
		constants->crop_bottom = static_cast<float>(cropBottom);
		immediateContext->Unmap(context->crop_buffer, 0);
	}

	D3D11_VIEWPORT viewport = {};
	viewport.Width = static_cast<float>(context->render_width);
	viewport.Height = static_cast<float>(context->render_height);
	viewport.MinDepth = 0.0f;
	viewport.MaxDepth = 1.0f;

	immediateContext->OMSetRenderTargets(1, &context->render_target_view, nullptr);
	immediateContext->RSSetViewports(1, &viewport);
	immediateContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
	immediateContext->VSSetShader(context->vertex_shader, nullptr, 0);
	immediateContext->VSSetConstantBuffers(0, 1, &context->crop_buffer);
	immediateContext->PSSetShader(context->pixel_shader, nullptr, 0);
	immediateContext->PSSetShaderResources(0, 2, views);
	immediateContext->PSSetSamplers(0, 1, &context->sampler_state);
	immediateContext->Draw(3, 0);

	ID3D11ShaderResourceView *nullViews[2] = {};
	immediateContext->PSSetShaderResources(0, 2, nullViews);

	context->swap_chain->Present(0, 0);

	return 0;
}

int create_video_decoder(int codec_id, void **handle)
{
	return create_video_decoder_with_options(codec_id, 0, handle);
}

int is_video_decoder_hardware_accelerated(void *handle)
{
	if (!handle)
		return 0;

	const auto context = static_cast<VideoDecoderContext *>(handle);
	return context->hardware_enabled ? 1 : 0;
}

int scale_decoded_video_frame(void *handle, void *scalerHandle, void *scaledBuffer, int scaledBufferStride)
{
#if _DEBUG
	if (!handle || !scalerHandle || !scaledBuffer)
		return -1;
#endif

	auto context = static_cast<VideoDecoderContext *>(handle);
	const auto scalerContext = static_cast<ScalerContext *>(scalerHandle);
	AVFrame *sourceFrame = context->active_frame ? context->active_frame : context->frame;

	if (scalerContext->source_top != 0 || scalerContext->source_left != 0)
	{
		const AVPixFmtDescriptor *sourceFmtDesc = av_pix_fmt_desc_get(scalerContext->source_pixel_format);

		if (!sourceFmtDesc)
			return -4;

		const int x_shift = sourceFmtDesc->log2_chroma_w;
		const int y_shift = sourceFmtDesc->log2_chroma_h;
		
		uint8_t *srcData[8];

		srcData[0] = sourceFrame->data[0] + scalerContext->source_top * sourceFrame->linesize[0] + scalerContext->source_left;
		srcData[1] = sourceFrame->data[1] + (scalerContext->source_top >> y_shift) * sourceFrame->linesize[1] + (scalerContext->source_left >> x_shift);
		srcData[2] = sourceFrame->data[2] + (scalerContext->source_top >> y_shift) * sourceFrame->linesize[2] + (scalerContext->source_left >> x_shift);
		srcData[3] = nullptr;
		srcData[4] = nullptr;
		srcData[5] = nullptr;
		srcData[6] = nullptr;
		srcData[7] = nullptr;

		sws_scale(scalerContext->sws_context, srcData, sourceFrame->linesize, 0,
			scalerContext->source_height, reinterpret_cast<uint8_t **>(&scaledBuffer), &scaledBufferStride);
	}
	else
	{
		sws_scale(scalerContext->sws_context, sourceFrame->data, sourceFrame->linesize, 0,
			scalerContext->source_height, reinterpret_cast<uint8_t **>(&scaledBuffer), &scaledBufferStride);
	}

	return 0;
}

void remove_video_decoder(void *handle)
{
	if (!handle)
		return;

	auto context = static_cast<VideoDecoderContext *>(handle);

	release_render_resources(context);
	
	if (context->av_codec_context)
	{
		avcodec_free_context(&context->av_codec_context);
	}

	av_free(context->input_buffer);
	av_buffer_unref(&context->hw_device_ctx);
	av_frame_free(&context->software_frame);
	av_frame_free(&context->frame);
	av_free(context);
}

int create_video_scaler(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int sourcePixelFormat,
	int scaledWidth, int scaledHeight, int scaledPixelFormat, int quality, void **handle)
{
	if (!handle)
		return -1;

	auto context = static_cast<ScalerContext *>(av_mallocz(sizeof(ScalerContext)));

	const auto sourceAvPixelFormat = static_cast<AVPixelFormat>(sourcePixelFormat);
	const auto scaledAvPixelFormat = static_cast<AVPixelFormat>(scaledPixelFormat);

	if (!context)
		return -2;

	SwsContext *swsContext = sws_getContext(sourceWidth, sourceHeight, sourceAvPixelFormat, scaledWidth, scaledHeight,
		scaledAvPixelFormat, quality, nullptr, nullptr, nullptr);
	
	if (!swsContext)
	{
		remove_video_scaler(context);
		return -3;
	}

	context->sws_context = swsContext;
	context->source_left = sourceLeft;
	context->source_top = sourceTop;
	context->source_height = sourceHeight;
	context->source_pixel_format = sourceAvPixelFormat;
	context->scaled_width = scaledWidth;
	context->scaled_height = scaledHeight;
	context->scaled_pixel_format = scaledAvPixelFormat;

	*handle = context;
	return 0;
}

void remove_video_scaler(void *handle)
{
	if (!handle)
		return;

	const auto context = static_cast<ScalerContext *>(handle);

	sws_freeContext(context->sws_context);
	av_free(context);
}

